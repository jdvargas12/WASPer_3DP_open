using System.Diagnostics;
using System.Net.WebSockets;

using WASPer.XR.Core;
using WASPer.XR.WebViewer;

// M2 host: a static three.js viewer page plus one endpoint that hands it a
// WASPerPrintJob as JSON. M5's live link (added 2026-08-19) reuses this same
// host rather than replacing it, exactly as originally planned below: the
// wwwroot page and the WASPer.XR.Core reference both carry forward
// unchanged, and /live/push + /live/view (LiveJobHub.cs) sit alongside
// /api/job as an additive path -- a static file-based load still works
// exactly as before for anyone who exports and points the viewer at a file.
//
// The URL is fixed here rather than left to launchSettings.json (which only
// applies to `dotnet run`/F5, not a published, directly-double-clicked exe)
// so dev and the standalone-package build (see scripts/
// Package-StandaloneViewer.ps1) behave identically.
//
// M6 (Mobile and QR Connection, added 2026-08-19): binds 0.0.0.0 rather than
// localhost specifically, so phones/tablets on the same LAN can reach this
// server too, not just the machine it runs on. 0.0.0.0 still answers
// localhost/127.0.0.1 requests exactly as before -- Test-ViewerRunning in
// Start-WASPerWebViewer.ps1 and every other localhost-based check in this
// codebase keep working unchanged. LocalBrowseUrl (below) stays a real,
// navigable "http://localhost:..." address for the auto-open/launcher path,
// since "http://0.0.0.0:..." isn't reliably openable as a URL on all
// platforms/browsers the way binding to it as a listen address is.
const string DefaultListenUrl = "http://0.0.0.0:5252";
const string DefaultLocalBrowseUrl = "http://localhost:5252";
string listenUrl = Environment.GetEnvironmentVariable("WASPER_VIEWER_LISTEN_URL")
    ?? DefaultListenUrl;
string localBrowseUrl = Environment.GetEnvironmentVariable("WASPER_VIEWER_BROWSE_URL")
    ?? DefaultLocalBrowseUrl;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls(listenUrl);
WebApplication app = builder.Build();

// Opens the browser once Kestrel is actually listening, so a double-clicked
// published exe behaves like an application rather than a server someone
// has to know to navigate to manually -- the whole point of a standalone
// package. Wrapped in try/catch because Process.Start with
// UseShellExecute=true can fail on locked-down machines; a failed
// auto-launch shouldn't crash the viewer, just mean the user opens the URL
// themselves.
app.Lifetime.ApplicationStarted.Register(() =>
{
    Console.WriteLine($"WASPer Process Viewer running at {localBrowseUrl} " +
        "(also reachable from other devices on this network -- see the QR code in Sm01's " +
        "Process Viewer tab) -- close this window to stop.");

    // Set by Start-WASPerWebViewer.ps1 (Sm01's "Open Web Viewer" button) when
    // it starts this process itself: that script opens the browser to a
    // job-specific URL (?path=...) once the server is confirmed listening,
    // so this bare-URL auto-open would otherwise pop a redundant second tab.
    if (Environment.GetEnvironmentVariable("WASPER_VIEWER_NO_AUTOLAUNCH") == "1")
        return;

    try
    {
        // localBrowseUrl, not listenUrl -- "http://0.0.0.0:5252" is a valid bind address but
        // not reliably openable as an actual browser URL across platforms.
        Process.Start(new ProcessStartInfo(localBrowseUrl) { UseShellExecute = true });
    }
    catch
    {
        Console.WriteLine("Could not open a browser automatically -- open the URL above manually.");
    }
});

app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = context =>
    {
        context.Context.Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
        context.Context.Response.Headers.Pragma = "no-cache";
        context.Context.Response.Headers.Expires = "0";
    }
});

// Must be registered before the /live/* endpoints below -- UseWebSockets is what makes
// HttpContext.WebSockets.AcceptWebSocketAsync() available to them at all. Placed here rather
// than deferred to right before those endpoints purely to keep it visually next to the other
// app.Use*/app.Map* pipeline setup; ordering relative to /api/job and /api/study (both plain
// GETs, unaffected by this) doesn't matter.
app.UseWebSockets();
var liveHub = new LiveJobHub();

app.MapGet("/api/job", (string? path) =>
{
    try
    {
        // ?path=<file> points at any real .wasperxr export -- binary
        // (schema 0.2.0, what Gc07 actually writes) or JSON (schema 0.1.0) --
        // auto-detected by WasperXrPackageImport.FromFile. A missing path is
        // intentionally not replaced with the development fixture: the normal
        // Sm01 workflow opens a bare viewer and supplies the job over Live Link.
        if (string.IsNullOrWhiteSpace(path))
            return Results.BadRequest(new { error = "No .wasperxr file path was provided." });

        string sourcePath = path;

        if (!File.Exists(sourcePath))
            return Results.NotFound(new { error = $"File not found: {sourcePath}" });

        // Round-trips through WASPer.XR.Core on purpose, rather than serving
        // the .wasperxr file directly: the browser only ever sees the
        // platform-independent WASPerPrintJob shape, never the legacy export
        // format, which is exactly the boundary Phase 0 froze.
        WASPerPrintJob job = WasperXrPackageImport.FromFile(sourcePath);
        string jobJson = WasperXrJson.Serialize(job);
        return Results.Text(jobJson, "application/json");
    }
    catch (Exception ex)
    {
        // Surface the real reason in the response body rather than a bare
        // 500 -- this endpoint is meant to be pointed at files nobody else
        // has tested yet, so a clear error matters more than a clean one.
        return Results.Problem(detail: ex.ToString(), statusCode: 500, title: "Failed to load .wasperxr file");
    }
});

// Study Dashboard feature: reads a study.json (written by Sm01's Cartesian
// study runner, WasperStudyStorage.Save in the main project) and returns
// the platform-independent StudySnapshot -- every iteration's parameters
// and KPIs, plus the native Dashboard tab's saved chart configuration --
// so the browser Dashboard panel opens showing the same chart selections
// the user already has configured natively. A study is optional, so the Dashboard panel
// prompts for a path itself rather than assuming one.
app.MapGet("/api/study", (string? path) =>
{
    try
    {
        // Mirrors /api/job's fallback: an empty ?path= tries a bundled
        // SampleData\study.json first (Package-StandaloneViewer.ps1's
        // -StudyFile bundles one there under the same convention as the
        // job's own SampleData\wasper-xr-sample.wasperxr.json), so a
        // standalone package's Dashboard button works with zero typing too.
        string? sourcePath = string.IsNullOrWhiteSpace(path) ? DefaultStudyPathOrNull() : path;

        if (sourcePath == null)
            return Results.NotFound(new { error = "No study.json path given and no bundled study found." });

        if (!File.Exists(sourcePath))
            return Results.NotFound(new { error = $"File not found: {sourcePath}" });

        StudySnapshot study = WasperStudyImport.FromFile(sourcePath);
        string studyJson = WasperXrJson.Serialize(study);
        return Results.Text(studyJson, "application/json");
    }
    catch (Exception ex)
    {
        return Results.Problem(detail: ex.ToString(), statusCode: 500, title: "Failed to load study.json");
    }
});

// M5 live link. /live/push is the one Grasshopper (Sm01/Gc07's
// WasperLiveViewerClient) connects to, sending raw .wasperxr binary messages for scene changes
// and tiny JSON text messages for sim_par-only updates; /live/view is what the browser connects
// to, receiving either complete jobs or lightweight progress messages. See LiveJobHub.cs --
// both handlers here just do the WebSocket handshake and hand the resulting
// socket off to it.
app.Map("/live/push", async (HttpContext context) =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }
    string session = NormalizeSession(context.Request.Query["session"]);
    using WebSocket socket = await context.WebSockets.AcceptWebSocketAsync();
    await liveHub.RunPushLoopAsync(socket, session, context.RequestAborted);
});

app.Map("/live/view", async (HttpContext context) =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }
    string session = NormalizeSession(context.Request.Query["session"]);
    // Browser live updates can contain large arrays. Per-message deflate is negotiated by the
    // browser automatically and substantially reduces phone/hotspot traffic. This local viewer
    // carries no secrets, so compression's cross-origin side-channel warning is not applicable.
    using WebSocket socket = await context.WebSockets.AcceptWebSocketAsync(
        new WebSocketAcceptContext { DangerousEnableCompression = true });
    await liveHub.RunViewLoopAsync(socket, session, context.RequestAborted);
});

// M5's closing deliverable, "Viewer status" (added 2026-08-19): a plain HTTP GET rather than
// another WebSocket -- Sm01 only needs an occasional snapshot, not a push channel, and a GET is
// far simpler to poll from the Grasshopper side than adding a receive loop to the existing
// one-way /live/push connection. Polled by WASPer_Sm01ProcessViewerController's
// TryRefreshLiveViewerStatus so the Process Viewer tab can show whether a browser is actually
// connected, instead of pushing live updates into the void with no feedback.
app.MapGet("/live/status", (string? session) =>
{
    string normalizedSession = NormalizeSession(session);
    return Results.Json(new
    {
        session = normalizedSession,
        viewerCount = liveHub.ViewerCount(normalizedSession)
    });
});

app.Run();

static string? DefaultStudyPathOrNull()
{
    string candidate = Path.Combine(AppContext.BaseDirectory, "SampleData", "study.json");
    return File.Exists(candidate) ? candidate : null;
}

static string NormalizeSession(string? session)
{
    if (string.IsNullOrWhiteSpace(session))
        return "default";

    string normalized = new string(session
        .Trim()
        .Where(character => char.IsLetterOrDigit(character) || character is '-' or '_')
        .Take(128)
        .ToArray());
    return string.IsNullOrWhiteSpace(normalized) ? "default" : normalized;
}
