using System;
using System.Globalization;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace WASPer_3DP.Components._5_0_Gcode
{
    /// <summary>
    /// M5 live link, GH-side half: a thin wrapper around <see cref="ClientWebSocket"/> that
    /// pushes a raw .wasperxr binary payload (the exact bytes
    /// <see cref="WasperXrBinaryPackage.WriteToBytes"/> produces -- same container, just sent
    /// over a socket instead of written to disk) to WASPer.XR.WebViewer's <c>/live/push</c>
    /// endpoint every time Sm01/Gc07's current path changes.
    /// </summary>
    /// <remarks>
    /// Deliberately simple for this first slice: one lazily-opened connection, reconnected on
    /// the next push attempt if it dropped (server not started yet, server restarted, network
    /// hiccup), no retry backoff beyond the caller's own debounce interval
    /// (see wsp_Sm01_WASPer_Study_Manager's TryPushLiveUpdate). A failed push throws back to
    /// the caller, which is expected to swallow/log it rather than surface a Grasshopper
    /// runtime error for every dropped live frame -- the next successful push simply
    /// supersedes it, same tolerance the WebViewer's own /live/view cache assumes.
    /// All calls are serialized through a semaphore so a slow send can't race a reconnect
    /// attempt from the next debounce tick.
    /// </remarks>
    internal sealed class WasperLiveViewerClient : IDisposable
    {
        private readonly Uri _pushUri;
        private readonly SemaphoreSlim _sendLock = new SemaphoreSlim(1, 1);
        private ClientWebSocket _socket;
        private bool _disposed;

        internal WasperLiveViewerClient(Uri pushUri)
        {
            _pushUri = pushUri ?? throw new ArgumentNullException(nameof(pushUri));
        }

        internal async Task PushAsync(byte[] payload, CancellationToken cancellationToken)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(WasperLiveViewerClient));
            if (payload == null || payload.Length == 0)
                return;

            await SendAsync(
                new ArraySegment<byte>(payload),
                WebSocketMessageType.Binary,
                cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Sends only externally-driven playback progress. This avoids rebuilding and moving the
        /// complete XR package when Sm05's sim_par changes but the path and context stay static.
        /// </summary>
        internal Task PushSimulationAsync(
            bool externalControl,
            double simulationParameter,
            CancellationToken cancellationToken)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(WasperLiveViewerClient));
            double value = Math.Max(0.0, Math.Min(1.0, simulationParameter));
            string json = "{\"type\":\"simulation\",\"externalControl\":" +
                (externalControl ? "true" : "false") +
                ",\"simulationParameter\":" +
                value.ToString("G17", CultureInfo.InvariantCulture) + "}";
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            return SendAsync(
                new ArraySegment<byte>(bytes),
                WebSocketMessageType.Text,
                cancellationToken);
        }

        private async Task SendAsync(
            ArraySegment<byte> payload,
            WebSocketMessageType messageType,
            CancellationToken cancellationToken)
        {
            await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
                await _socket.SendAsync(
                    payload,
                    messageType,
                    true,
                    cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // A failed send likely means the socket is dead even if .State still reads
                // Open (e.g. the server process was closed without a clean handshake) --
                // drop it so the next push attempt opens a fresh connection instead of
                // repeatedly failing against a socket that will never recover.
                DisposeSocket();
                throw;
            }
            finally
            {
                _sendLock.Release();
            }
        }

        private async Task EnsureConnectedAsync(CancellationToken cancellationToken)
        {
            if (_socket != null && _socket.State == WebSocketState.Open)
                return;

            DisposeSocket();
            var socket = new ClientWebSocket();
            await socket.ConnectAsync(_pushUri, cancellationToken).ConfigureAwait(false);
            _socket = socket;
        }

        private void DisposeSocket()
        {
            ClientWebSocket socket = _socket;
            _socket = null;
            if (socket == null)
                return;
            try
            {
                socket.Dispose();
            }
            catch
            {
                // Best-effort cleanup only -- disposal failures here would just mask
                // whatever the real (already-handled) send/connect error was.
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            DisposeSocket();
            _sendLock.Dispose();
        }
    }
}
