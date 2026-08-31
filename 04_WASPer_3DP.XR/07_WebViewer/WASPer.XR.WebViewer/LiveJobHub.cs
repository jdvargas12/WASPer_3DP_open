using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

using WASPer.XR.Core;

namespace WASPer.XR.WebViewer;

/// <summary>
/// M5 live link, server-side half: an in-memory relay between one Grasshopper "push" connection
/// (Sm01/Gc07's WasperLiveViewerClient) and any number of browser "view" connections.
/// </summary>
/// <remarks>
/// The push side receives the exact same .wasperxr binary payload for structural job updates
/// <see cref="Components._5_0_Gcode.WasperXrBinaryPackage" /> already writes to disk (magic +
/// header + gzip'd body), just over a socket instead of a file -- decoded once per push through
/// the same <see cref="WasperXrPackageImport.FromBinary" /> reader /api/job already uses, then
/// re-serialized to the platform-independent WASPerPrintJob JSON and broadcast to every
/// connected viewer. Sm05 simulation-only changes use a tiny text frame and never decode or
/// serialize the job again. The latest job and progress are cached for newly connected viewers.
///
/// Deliberately simple for this first slice: single in-memory instance (matches this project's
/// "one local server on localhost" trust model, no auth, no persistence), no arbitration between
/// multiple simultaneous pushers (the practical case is one Rhino session at a time -- if a
/// second one connects, whichever push arrives most recently is what viewers see, same as two
/// people saving over the same file), and a dead viewer socket is only cleaned up when its own
/// receive loop notices the close, not proactively from the broadcast path.
/// </remarks>
internal sealed class LiveJobHub
{
    private sealed class ViewerState(WebSocket socket)
    {
        public WebSocket Socket { get; } = socket;
        public string? ContextSignature { get; set; }
    }

    private sealed record CachedJob(string Json, string ContextSignature);

    private readonly Dictionary<string, List<ViewerState>> _viewersBySession = new();
    private readonly Dictionary<string, CachedJob> _lastJobBySession = new();
    private sealed record SimulationState(bool ExternalControl, double Parameter);

    private readonly Dictionary<string, SimulationState> _lastSimulationBySession = new();
    private readonly object _lock = new();

    /// <summary>
    /// Number of currently-open browser "view" connections (M5's closing deliverable, "Viewer
    /// status", added 2026-08-19). Read by Program.cs's /live/status endpoint, which Sm01 polls
    /// so Grasshopper can tell whether anyone is actually watching a live push land, rather than
    /// pushing into the void with no feedback.
    /// </summary>
    public int ViewerCount(string session)
    {
        lock (_lock)
        {
            return _viewersBySession.TryGetValue(session, out List<ViewerState>? viewers)
                ? viewers.Count(viewer => viewer.Socket.State == WebSocketState.Open)
                : 0;
        }
    }

    /// <summary>
    /// Runs for the lifetime of one Grasshopper push connection: reads whole binary messages
    /// (a .wasperxr binary container for structural changes or a tiny JSON text frame for
    /// simulation progress), and broadcasts the appropriate update to every viewer.
    /// </summary>
    public async Task RunPushLoopAsync(
        WebSocket socket,
        string session,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        using var payload = new MemoryStream();

        while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
        {
            payload.SetLength(0);
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(buffer, cancellationToken);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await SafeCloseAsync(socket, cancellationToken);
                    return;
                }
                payload.Write(buffer, 0, result.Count);
            }
            while (!result.EndOfMessage);

            if (payload.Length == 0)
                continue;

            try
            {
                if (result.MessageType == WebSocketMessageType.Text)
                {
                    string message = Encoding.UTF8.GetString(payload.GetBuffer(), 0, (int)payload.Length);
                    if (TryReadSimulationState(
                        message,
                        out bool externalControl,
                        out double simulationParameter))
                    {
                        await BroadcastSimulationAsync(
                            session,
                            externalControl,
                            simulationParameter,
                            cancellationToken);
                    }
                    continue;
                }
                if (result.MessageType != WebSocketMessageType.Binary)
                    continue;
                payload.Position = 0;
                WASPerPrintJob job = WasperXrPackageImport.FromBinary(payload);
                await BroadcastAsync(session, job, cancellationToken);
            }
            catch (Exception ex)
            {
                // A malformed or partial push shouldn't tear down the connection -- the next
                // push from Sm01's debounce loop supersedes it a fraction of a second later.
                Console.WriteLine($"Live push decode failed: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Runs for the lifetime of one browser connection: sends the cached last-known job
    /// immediately (if any), then just waits -- all further messages arrive via
    /// <see cref="BroadcastAsync" /> from the push side, not from anything this loop reads.
    /// </summary>
    public async Task RunViewLoopAsync(
        WebSocket socket,
        string session,
        CancellationToken cancellationToken)
    {
        var viewer = new ViewerState(socket);
        lock (_lock)
        {
            if (!_viewersBySession.TryGetValue(session, out List<ViewerState>? viewers))
            {
                viewers = new List<ViewerState>();
                _viewersBySession[session] = viewers;
            }
            viewers.Add(viewer);
        }
        try
        {
            CachedJob? snapshot;
            lock (_lock)
                _lastJobBySession.TryGetValue(session, out snapshot);
            if (snapshot != null)
            {
                await SendAsync(socket, snapshot.Json, cancellationToken);
                viewer.ContextSignature = snapshot.ContextSignature;
            }
            SimulationState? simulation;
            lock (_lock)
                simulation = _lastSimulationBySession.TryGetValue(session, out SimulationState? value)
                    ? value
                    : null;
            if (simulation != null)
                await SendSimulationAsync(
                    socket,
                    simulation.ExternalControl,
                    simulation.Parameter,
                    cancellationToken);

            var buffer = new byte[4 * 1024];
            while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                WebSocketReceiveResult result = await socket.ReceiveAsync(buffer, cancellationToken);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await SafeCloseAsync(socket, cancellationToken);
                    break;
                }
                // Viewers are read-only clients from the hub's point of view -- any other
                // inbound frame (there shouldn't be any from the current index.html client) is
                // simply drained and ignored so the loop keeps servicing Close/disconnect.
            }
        }
        finally
        {
            lock (_lock)
            {
                if (_viewersBySession.TryGetValue(session, out List<ViewerState>? viewers))
                {
                    viewers.Remove(viewer);
                    if (viewers.Count == 0)
                        _viewersBySession.Remove(session);
                }
            }
        }
    }

    private async Task BroadcastAsync(
        string session,
        WASPerPrintJob job,
        CancellationToken cancellationToken)
    {
        string contextSignature = ComputeContextSignature(job.ContextMeshes);
        string fullJson = WasperXrJson.Serialize(job);
        string deltaJson = WasperXrJson.Serialize(job with { ContextMeshes = null });
        List<ViewerState> targets;
        lock (_lock)
        {
            // The cached snapshot stays self-contained so a viewer joining after this push
            // receives the complete scene. Already-connected viewers get the lightweight
            // context-free variant whenever their cached context still matches.
            _lastJobBySession[session] = new CachedJob(fullJson, contextSignature);
            if (job.Metadata.DisablePlayback)
                _lastSimulationBySession[session] = new SimulationState(
                    true,
                    job.Metadata.SimulationParameter);
            else
                _lastSimulationBySession.Remove(session);
            targets = _viewersBySession.TryGetValue(session, out List<ViewerState>? viewers)
                ? viewers.Where(viewer => viewer.Socket.State == WebSocketState.Open).ToList()
                : new List<ViewerState>();
        }
        foreach (ViewerState viewer in targets)
        {
            try
            {
                bool needsContext = !string.Equals(
                    viewer.ContextSignature,
                    contextSignature,
                    StringComparison.Ordinal);
                await SendAsync(
                    viewer.Socket,
                    needsContext ? fullJson : deltaJson,
                    cancellationToken);
                viewer.ContextSignature = contextSignature;
            }
            catch
            {
                // A send failing here means that viewer's socket is already dead; its own
                // RunViewLoopAsync will notice on its next receive and remove it from _viewers.
                // Not removing it inline avoids mutating _viewers while BroadcastAsync is still
                // iterating the snapshot taken above.
            }
        }
    }

    private async Task BroadcastSimulationAsync(
        string session,
        bool externalControl,
        double simulationParameter,
        CancellationToken cancellationToken)
    {
        double value = Math.Clamp(simulationParameter, 0.0, 1.0);
        List<ViewerState> targets;
        lock (_lock)
        {
            _lastSimulationBySession[session] = new SimulationState(externalControl, value);
            targets = _viewersBySession.TryGetValue(session, out List<ViewerState>? viewers)
                ? viewers.Where(viewer => viewer.Socket.State == WebSocketState.Open).ToList()
                : new List<ViewerState>();
        }
        foreach (ViewerState viewer in targets)
        {
            try
            {
                await SendSimulationAsync(
                    viewer.Socket,
                    externalControl,
                    value,
                    cancellationToken);
            }
            catch
            {
                // The viewer receive loop owns stale-socket cleanup.
            }
        }
    }

    private static bool TryReadSimulationState(
        string json,
        out bool externalControl,
        out double simulationParameter)
    {
        externalControl = true;
        simulationParameter = 0.0;
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            bool valid = root.TryGetProperty("type", out JsonElement type) &&
                string.Equals(type.GetString(), "simulation", StringComparison.Ordinal) &&
                root.TryGetProperty("simulationParameter", out JsonElement value) &&
                value.TryGetDouble(out simulationParameter) &&
                double.IsFinite(simulationParameter);
            if (valid && root.TryGetProperty("externalControl", out JsonElement external))
                externalControl = external.ValueKind == JsonValueKind.True;
            return valid;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    // A deterministic content signature distinguishes a genuinely changed/cleared Sm05 scene
    // from the same static context arriving with another simulation frame. Hashing all values
    // avoids false reuse when a mesh deforms without changing its vertex/face counts or bounds.
    private static string ComputeContextSignature(IReadOnlyList<ContextMesh>? meshes)
    {
        const ulong offsetBasis = 14695981039346656037UL;
        ulong hash = offsetBasis;
        AddInt(ref hash, meshes?.Count ?? 0);
        foreach (ContextMesh mesh in meshes ?? Array.Empty<ContextMesh>())
        {
            AddString(ref hash, mesh.Id);
            AddByte(ref hash, mesh.Red);
            AddByte(ref hash, mesh.Green);
            AddByte(ref hash, mesh.Blue);
            AddByte(ref hash, mesh.Alpha);
            AddVectors(ref hash, mesh.Vertices);
            AddVectors(ref hash, mesh.Normals);
            AddInt(ref hash, mesh.TriangleIndices.Count);
            foreach (int index in mesh.TriangleIndices)
                AddLong(ref hash, index);
        }
        return hash.ToString("X16");
    }

    private static void AddVectors(ref ulong hash, IReadOnlyList<Vec3> values)
    {
        AddInt(ref hash, values.Count);
        foreach (Vec3 value in values)
        {
            AddLong(ref hash, BitConverter.DoubleToInt64Bits(value.X));
            AddLong(ref hash, BitConverter.DoubleToInt64Bits(value.Y));
            AddLong(ref hash, BitConverter.DoubleToInt64Bits(value.Z));
        }
    }

    private static void AddString(ref ulong hash, string? value)
    {
        string text = value ?? string.Empty;
        AddInt(ref hash, text.Length);
        foreach (char character in text)
            AddLong(ref hash, character);
    }

    private static void AddInt(ref ulong hash, int value) => AddLong(ref hash, value);

    private static void AddLong(ref ulong hash, long value)
    {
        unchecked
        {
            for (int shift = 0; shift < 64; shift += 8)
                AddByte(ref hash, (byte)(value >> shift));
        }
    }

    private static void AddByte(ref ulong hash, byte value)
    {
        unchecked
        {
            hash ^= value;
            hash *= 1099511628211UL;
        }
    }

    private static Task SendAsync(WebSocket socket, string json, CancellationToken cancellationToken)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(json);
        return socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
    }

    private static Task SendSimulationAsync(
        WebSocket socket,
        bool externalControl,
        double simulationParameter,
        CancellationToken cancellationToken)
    {
        string json = JsonSerializer.Serialize(new
        {
            type = "simulation",
            externalControl,
            simulationParameter
        });
        return SendAsync(socket, json, cancellationToken);
    }

    private static async Task SafeCloseAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        try
        {
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, cancellationToken);
        }
        catch
        {
            // Best-effort -- the connection is going away either way.
        }
    }
}
