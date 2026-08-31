using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Grasshopper;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Parameters;
using Grasshopper.Kernel.Types;
using Newtonsoft.Json.Linq;
using WASPer_3DP.Components._5_0_Gcode;

namespace WASPer_3DP.Components._1_2_Studies
{
    public sealed partial class wsp_Sm01_WASPer_Study_Manager
    {
        private WasperPrintPath _currentProcessViewerPath;

        // Set from Sm01's "xr_pack" input (Sm05 XR Scene Params, added 2026-08-19). Only
        // SimulationParameterConnected disables the web viewer's own playback controls when an
        // external source drives the print position. Sm05's viewer-ready context meshes are also
        // passed into both file export and live packages.
        private WasperXrScenePack _currentXrScenePack;
        private string _lastProcessViewerJsonPath = string.Empty;
        private string _processViewerStatus = "Ready.";
        private string _dumpStudyStatus = string.Empty;

        // M5 live link (2026-08-19), reworked into an explicit "Live" toggle the same day after
        // real-world use turned up a confusing failure mode: the old design only ever pushed
        // live (or started the web viewer server at all) once "Open Web Viewer" had been
        // clicked, so a phone that scanned the QR code before that click just got
        // ERR_CONNECTION_TIMED_OUT against a server that was never asked to start. The server
        // now starts on its own (see EnsureWebViewerServerRunning, called from
        // UpdateProcessViewerWindow) as soon as there's something worth viewing, independent of
        // any button click, and _liveEnabled (on by default) purely controls whether
        // UpdateProcessViewerWindow's automatic per-solve push/status-poll happens -- turning it
        // off doesn't stop the server or disconnect anyone already viewing, just pauses the
        // automatic push, with PushChangeNow below as the manual alternative while it's off.
        // _lastLivePushUtc + LivePushMinInterval debounce rapid-fire solves (e.g. dragging a
        // slider) to at most one push per interval, mirroring the Dashboard-render throttling
        // fix from the same week's tab-slowness investigation.
        private static readonly TimeSpan LivePushMinInterval = TimeSpan.FromMilliseconds(400);
        private bool _liveEnabled = true;
        private WasperLiveViewerClient _liveViewerClient;
        private string _liveViewerClientSession = string.Empty;
        private DateTime _lastLivePushUtc = DateTime.MinValue;
        private bool _deferredLivePushScheduled;
        // Once a complete job has reached the socket, Sm05 sim_par changes can use a tiny text
        // frame instead of rebuilding the same path/context package. These identities are stable
        // across an isolated sim_par solve because Sm05 reuses its cached context-mesh list.
        private bool _hasLiveStructureSnapshot;
        private WasperPrintPath _lastLiveStructurePath;
        private object _lastLiveContextIdentity;
        private string _lastLiveKpiSignature = string.Empty;
        private bool _lastLiveExternalSimulation;
        private double _lastLiveSimulationParameter = double.NaN;
        // The first live push can race the hidden viewer-server startup. Keep the expensive
        // binary result so a failed connection/retry does not serialize and gzip the same large
        // path and context scene again on the Grasshopper UI thread.
        private byte[] _cachedLivePackageBytes;
        private WasperPrintPath _cachedLivePackagePath;
        private object _cachedLivePackageContextIdentity;
        private string _cachedLivePackageKpiSignature = string.Empty;
        private readonly object _liveSimulationQueueLock = new object();
        private bool _liveSimulationSendInProgress;
        private bool _pendingLiveExternalSimulation;
        private double? _pendingLiveSimulationParameter;
        // Guards EnsureWebViewerServerRunning so the hidden launcher process is only ever spawned
        // once per component lifetime (the launcher script itself already reuses an
        // already-running server rather than starting a second one, but there's no reason to ask
        // it more than once). Reset to false on a launch failure so a later solve gets to retry.
        private bool _webViewerServerStartRequested;
        private bool _webViewerServerStartupInProgress;
        private bool _webViewerRuntimeAvailable = true;
        private string _webViewerRuntimeStatus = "Web viewer runtime ready.";
        private DateTime _lastWebViewerRuntimeCheckUtc = DateTime.MinValue;

        // M5's closing deliverable, "Viewer status" (2026-08-19): a plain HTTP GET against
        // /live/status (LiveJobHub.ViewerCount, WebViewer's Program.cs) rather than a second
        // WebSocket -- Sm01 only needs an occasional snapshot, not a push channel, so polling is
        // simpler than teaching the existing one-way /live/push connection to also receive.
        // Static/shared HttpClient per .NET guidance (a new one per call risks socket
        // exhaustion under rapid polling); short timeout so an unreachable server doesn't stall
        // a poll noticeably past its own interval.
        // Opening the actual browser tab moved here from Start-WASPerWebViewer.ps1's own
        // Start-Process call (2026-08-19): that script runs hidden and, once
        // MonitorWebViewerServerStartupAsync (then still combined with the browser-open step,
        // later split out into OpenWebViewerInBrowser) started redirecting its stdout/stderr for
        // error capture, its nested Start-Process/ShellExecute call to open a browser silently stopped
        // producing a visible window -- plausibly the redirected/pipe-backed standard handles
        // changing how ShellExecuteEx resolves an interactive Desktop from that deeper, hidden
        // process chain. OpenProcessViewerFolder/OpenDumpStudyFolder below already open Explorer
        // this same way, directly from GH's own interactive process, and that has been reliable
        // throughout this project -- doing the same for the browser sidesteps the whole hidden
        // child-process chain instead of chasing exactly why ShellExecute stopped working there.
        private const string LocalViewerBaseUrl = "http://localhost:5252/";
        private static readonly TimeSpan LiveStatusPollMinInterval = TimeSpan.FromSeconds(1.5);
        private static readonly HttpClient LiveStatusHttpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(2)
        };
        private DateTime _lastLiveStatusPollUtc = DateTime.MinValue;

        private void ExportProcessViewerJob(string folder, string jobId)
        {
            string normalizedFolder = string.IsNullOrWhiteSpace(folder)
                ? DefaultProcessViewerFolder()
                : folder.Trim();
            string normalizedJobId = string.IsNullOrWhiteSpace(jobId)
                ? DefaultProcessViewerJobId()
                : jobId.Trim();
            int revision = NextProcessViewerRevision(normalizedFolder, normalizedJobId);

            // _currentSet is the same merged, user-filtered global KPI set
            // the KPIs tab shows (Fabrication auto-extracted from the path
            // plus whatever Infill/material/thermal components are wired
            // into kpi_sets and enabled) -- reachable directly since this is
            // a partial class split across files, not a separate type.
            if (!wsp_Gc07_Export_XR_Package.TryExportPackage(
                _currentProcessViewerPath,
                1.0,
                normalizedFolder,
                normalizedJobId,
                revision,
                _version,
                out string jsonPath,
                out _,
                out string summary,
                out string error,
                _currentSet,
                _currentXrScenePack?.SimulationParameterConnected == true,
                _currentXrScenePack?.SimulationParameter ?? 1.0,
                _currentXrScenePack))
            {
                _processViewerStatus = error;
                _form?.SetProcessViewerResult(
                _lastProcessViewerJsonPath,
                error,
                TryResolveProcessViewerFiles(out _, out _, out _),
                TryCheckWebViewerRuntime(out string exportErrorRuntimeStatus),
                exportErrorRuntimeStatus);
                return;
            }

            _lastProcessViewerJsonPath = jsonPath;
            _processViewerStatus = summary;
            _lastWrittenFiles = (_lastWrittenFiles ?? new List<string>())
                .Where(path => !string.Equals(path, jsonPath, StringComparison.OrdinalIgnoreCase))
                .Append(jsonPath)
                .ToList();
            bool viewerAvailable = TryResolveProcessViewerFiles(out _, out _, out _);
            bool webRuntimeAvailable = TryCheckWebViewerRuntime(out string webRuntimeStatus);
            _form?.SetProcessViewerResult(
                jsonPath,
                summary,
                viewerAvailable,
                webRuntimeAvailable,
                webRuntimeStatus);
            OnObjectChanged(Grasshopper.Kernel.GH_ObjectEventType.Options);
            ExpireSolution(true);
        }

        private void LaunchProcessViewer(string jsonPath)
        {
            string jobPath = File.Exists(jsonPath)
                ? jsonPath
                : _lastProcessViewerJsonPath;
            if (!File.Exists(jobPath))
            {
                SetProcessViewerStatus("Export a viewer package before opening the viewer.");
                return;
            }
            if (!TryResolveProcessViewerFiles(
                out string gammaPath,
                out string patchPath,
                out string launcherPath))
            {
                SetProcessViewerStatus(ProcessViewerAvailabilityMessage());
                return;
            }

            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments =
                        "-NoProfile -ExecutionPolicy Bypass -File " + Quote(launcherPath) +
                        " -JobPath " + Quote(jobPath) +
                        " -GammaPath " + Quote(gammaPath) +
                        " -PatchPath " + Quote(patchPath),
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    WindowStyle = ProcessWindowStyle.Hidden
                };
                Process.Start(startInfo);
                SetProcessViewerStatus("Opening WASPer Process Viewer...");
            }
            catch (Exception exception)
            {
                SetProcessViewerStatus("Viewer launch failed: " + exception.Message);
            }
        }

        // Starts the local web viewer server on its own -- no button click required (2026-08-19
        // rework, see the field comment above _liveEnabled for the ERR_CONNECTION_TIMED_OUT
        // failure this replaces). Called from UpdateProcessViewerWindow every solve once there's
        // something worth viewing; _webViewerServerStartRequested makes every call after the
        // first a no-op. Deliberately does NOT open a browser tab itself -- that's
        // OpenWebViewerInBrowser's job now, a separate explicit action rather than a side effect
        // of the server coming up, so opening this on a phone via the Mobile Access QR code
        // doesn't also unexpectedly pop a browser window on the desktop machine.
        private void EnsureWebViewerServerRunning()
        {
            if (_webViewerServerStartRequested || _webViewerServerStartupInProgress)
                return;
            if (!TryCheckWebViewerRuntime(out string runtimeStatus))
            {
                SetProcessViewerStatus(runtimeStatus);
                return;
            }
            if (!TryResolveWebViewerLauncher(out string launcherPath))
                return; // silent here -- surfaced instead the moment something actually tries to use it (Open in Browser, Push Change, or a live-push/status-poll failure)
            _webViewerServerStartRequested = true;
            _webViewerServerStartupInProgress = true;

            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    // No -JobPath: the script's only job is guaranteeing the server is
                    // reachable, not building a URL -- see LocalViewerBaseUrl above.
                    Arguments = "-NoProfile -ExecutionPolicy Bypass -File " + Quote(launcherPath),
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    // Captured so a launcher-side failure (script throws, dotnet fails to bind,
                    // etc.) shows up on the status line instead of vanishing into a hidden
                    // process nobody can see the console of.
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
                process.Start();
                _ = MonitorWebViewerServerStartupAsync(process);
                SetProcessViewerStatus("Starting the WASPer web viewer server...");
            }
            catch (Exception exception)
            {
                _webViewerServerStartRequested = false; // never actually got a process running -- let a later solve retry
                _webViewerServerStartupInProgress = false;
                SetProcessViewerStatus("Web viewer server failed to start: " + exception.Message);
            }
        }

        // Opens a local browser tab pointed at the web viewer. Split off from server-starting
        // (2026-08-19, see EnsureWebViewerServerRunning above) so this is purely the "show me"
        // action -- calling it also (defensively, idempotently) makes sure the server's at least
        // been asked to start, in case this is somehow reached before UpdateProcessViewerWindow's
        // own automatic call has run.
        private void OpenWebViewerInBrowser(string jsonPath)
        {
            if (!TryCheckWebViewerRuntime(out string runtimeStatus))
            {
                SetProcessViewerStatus(runtimeStatus);
                return;
            }
            EnsureWebViewerServerRunning();

            string jobPath = File.Exists(jsonPath)
                ? jsonPath
                : (File.Exists(_lastProcessViewerJsonPath) ? _lastProcessViewerJsonPath : null);
            string targetUrl = BuildViewerUrl(LocalViewerBaseUrl, jobPath);

            try
            {
                // Direct from GH's own interactive process (UseShellExecute=true), the same
                // reliable pattern OpenProcessViewerFolder/OpenDumpStudyFolder already use for
                // Explorer -- see the LocalViewerBaseUrl comment for the history here.
                Process.Start(new ProcessStartInfo(targetUrl) { UseShellExecute = true });
                SetProcessViewerStatus("Opening WASPer web viewer...");
            }
            catch (Exception exception)
            {
                SetProcessViewerStatus("Could not open the web viewer: " + exception.Message);
            }
        }

        // "Live" toggle click handler (2026-08-19). Turning Live back on immediately force-pushes
        // and refreshes status, so re-enabling it doesn't leave the viewer showing a stale frame
        // until the next solve happens to occur.
        private void SetLiveEnabled(bool enabled)
        {
            _liveEnabled = enabled;
            _form?.SetLiveToggleState(enabled);
            if (!enabled)
                return;
            TryPushLiveUpdate(force: true);
            TryRefreshLiveViewerStatus(force: true);
        }

        // "Push Change" click handler -- only ever reachable while Live is off (the button that
        // calls this is disabled otherwise, see WASPer_Sm01ProcessViewerTab.cs), so this bypasses
        // TryPushLiveUpdate's _liveEnabled gate via manual: true rather than being a no-op.
        private void PushChangeNow()
        {
            if (!TryCheckWebViewerRuntime(out string runtimeStatus))
            {
                SetProcessViewerStatus(runtimeStatus);
                return;
            }
            EnsureWebViewerServerRunning();
            TryPushLiveUpdate(force: true, manual: true);
            TryRefreshLiveViewerStatus(force: true);
        }

        // Explicit scene/network ping from the Process Viewer tab. Unlike the periodic status
        // poll, this also regenerates LAN/hotspot QR links and force-pushes the latest complete
        // scene even when Live is paused. A dead server/client is recovered by the normal
        // failure path; MonitorWebViewerServerStartupAsync then retries the scene automatically.
        private void RefreshProcessViewerScene()
        {
            if (!TryCheckWebViewerRuntime(out string runtimeStatus))
            {
                SetProcessViewerStatus(runtimeStatus);
                return;
            }
            SetProcessViewerStatus("Refreshing XR scene, server, and mobile links...");
            RefreshMobileAccess();
            // Refresh is also an explicit recovery command. A server can disappear after an
            // earlier successful launch, so do not let the component-lifetime start flag turn
            // this click into a no-op. The launcher performs the authoritative endpoint check
            // and safely reuses a healthy process.
            if (!_webViewerServerStartupInProgress)
                _webViewerServerStartRequested = false;
            EnsureWebViewerServerRunning();
            TryPushLiveUpdate(force: true, manual: true);
            TryRefreshLiveViewerStatus(force: true);
        }

        // Waits on the hidden launcher process (which only ensures the server is up, see
        // LocalViewerBaseUrl above) and reports success/failure to the status line. The launcher
        // either reuses an already-running server (exits almost immediately) or starts one and
        // waits up to 120s for a cold build to come up (exits once that's done, or throws on
        // timeout) -- either way, once this process exits cleanly, the server is known-ready.
        private async Task MonitorWebViewerServerStartupAsync(Process process)
        {
            try
            {
                // Read stdout line-by-line and echo each line to the status bar as it arrives,
                // rather than batching with ReadToEndAsync (which only returns once the whole
                // process exits). A cold `dotnet run` can now take up to two minutes (see
                // Start-WASPerWebViewer.ps1's timeoutMs), and with a batched read the status line
                // just sat on one message for that entire span with no visible sign anything was
                // happening -- indistinguishable from actually being stuck.
                // stderr is drained concurrently into a buffer (not surfaced live, only if the
                // process ultimately fails) so its pipe can't fill up and block the child while
                // stdout is being read -- same concurrent-drain requirement ReadToEndAsync used
                // to satisfy by starting both tasks before awaiting either.
                var stdErrBuilder = new StringBuilder();
                Task stdErrTask = DrainStreamAsync(process.StandardError, stdErrBuilder);

                // No ConfigureAwait(false) anywhere in this method: the status-line updates touch
                // WinForms state that should resume on the UI thread's SynchronizationContext,
                // same reasoning as PushLiveSafeAsync/PollLiveViewerStatusSafeAsync above.
                string line;
                while ((line = await process.StandardOutput.ReadLineAsync()) != null)
                {
                    if (!string.IsNullOrWhiteSpace(line))
                        SetProcessViewerStatus("Starting the WASPer web viewer server -- " + line.Trim());
                }
                await process.WaitForExitAsync();
                await stdErrTask;
                string stdErr = stdErrBuilder.ToString();

                bool failed = process.ExitCode != 0 || !string.IsNullOrWhiteSpace(stdErr);
                if (!failed)
                {
                    _webViewerServerStartupInProgress = false;
                    SetProcessViewerStatus("Web viewer server is running.");
                    TryPushLiveUpdate(force: true);
                    TryRefreshLiveViewerStatus(force: true);
                    return;
                }

                string detail = !string.IsNullOrWhiteSpace(stdErr)
                    ? stdErr
                    : $"launcher exited with code {process.ExitCode} and no output";
                detail = detail.Trim();
                if (detail.Length > 400)
                    detail = detail.Substring(0, 400) + "...";
                SetProcessViewerStatus("Web viewer server failed to start: " + detail);
                _webViewerServerStartRequested = false; // let a later solve retry
                _webViewerServerStartupInProgress = false;
            }
            catch (Exception exception)
            {
                SetProcessViewerStatus("Web viewer server failed to start: " + exception.Message);
                _webViewerServerStartRequested = false;
                _webViewerServerStartupInProgress = false;
            }
            finally
            {
                process.Dispose();
            }
        }

        // Reads a redirected stream to completion into a buffer, without surfacing anything
        // live -- used for the launcher's stderr, which should stay silent unless the whole
        // thing ultimately fails, unlike stdout above which is echoed to the status bar line by
        // line as it arrives.
        private static async Task DrainStreamAsync(StreamReader reader, StringBuilder buffer)
        {
            string line;
            while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) != null)
                buffer.AppendLine(line);
        }

        // Pushes _currentProcessViewerPath to the WebViewer's live hub, debounced to at most
        // once per LivePushMinInterval so a rapid run of solves (dragging a slider, a fast
        // study sweep) doesn't flood the socket -- a skipped frame is harmless, the next one
        // supersedes it. Gated on _liveEnabled (the "Live" toggle, on by default) unless manual
        // is set, which is how PushChangeNow bypasses the gate while Live is off.
        // Fire-and-forget on purpose: SolveInstance must never block on network I/O, so the
        // actual send happens on a background Task and failures are logged to the status line
        // rather than thrown back into the solve.
        private void TryPushLiveUpdate(bool force = false, bool manual = false)
        {
            if (!_liveEnabled && !manual)
                return;

            WasperPrintPath path = _currentProcessViewerPath;
            if (path == null || !path.HasPoints || !path.HasMotionPlan)
                return;

            string session = LiveViewerSessionId();
            if (_liveViewerClient == null ||
                !string.Equals(_liveViewerClientSession, session, StringComparison.Ordinal))
            {
                _liveViewerClient?.Dispose();
                _liveViewerClient = new WasperLiveViewerClient(new Uri(
                    "ws://localhost:5252/live/push?session=" + Uri.EscapeDataString(session)));
                _liveViewerClientSession = session;
                _hasLiveStructureSnapshot = false;
                _lastLiveStructurePath = null;
                _lastLiveContextIdentity = null;
                _lastLiveKpiSignature = string.Empty;
                _lastLiveExternalSimulation = false;
                _lastLiveSimulationParameter = double.NaN;
            }

            bool externalSimulation = _currentXrScenePack?.SimulationParameterConnected == true;
            double simulationParameter = _currentXrScenePack?.SimulationParameter ?? 1.0;
            object contextIdentity = _currentXrScenePack?.ContextMeshes;
            string kpiSignature = LiveKpiSignature(_currentSet);
            bool structureUnchanged =
                !force &&
                !manual &&
                _hasLiveStructureSnapshot &&
                ReferenceEquals(path, _lastLiveStructurePath) &&
                ReferenceEquals(contextIdentity, _lastLiveContextIdentity) &&
                string.Equals(kpiSignature, _lastLiveKpiSignature, StringComparison.Ordinal);

            // Playback ownership is an independent, tiny state stream. Never gate its release
            // behind a healthy structural snapshot: if the large package is still building,
            // reconnecting, or failed previously, the browser may still have an older job and
            // must be told immediately that Sm05/Gc05 no longer owns playback.
            bool playbackStateChanged =
                externalSimulation != _lastLiveExternalSimulation ||
                (externalSimulation &&
                 Math.Abs(simulationParameter - _lastLiveSimulationParameter) > 1e-12);
            if (playbackStateChanged || force || manual)
            {
                _lastLiveExternalSimulation = externalSimulation;
                _lastLiveSimulationParameter = simulationParameter;
                QueueSimulationLiveUpdate(
                    _liveViewerClient,
                    externalSimulation,
                    simulationParameter);
            }

            if (structureUnchanged)
            {
                return;
            }

            if (!force && DateTime.UtcNow - _lastLivePushUtc < LivePushMinInterval)
            {
                ScheduleDeferredLivePush();
                return;
            }
            _lastLivePushUtc = DateTime.UtcNow;

            bool canReusePackage =
                !force &&
                !manual &&
                _cachedLivePackageBytes != null &&
                ReferenceEquals(path, _cachedLivePackagePath) &&
                ReferenceEquals(contextIdentity, _cachedLivePackageContextIdentity) &&
                string.Equals(
                    kpiSignature,
                    _cachedLivePackageKpiSignature,
                    StringComparison.Ordinal);

            byte[] bytes = _cachedLivePackageBytes;
            if (!canReusePackage &&
                !wsp_Gc07_Export_XR_Package.TryBuildLivePackageBytes(
                    path,
                    DefaultProcessViewerJobId(),
                    0,
                    _version,
                    _currentSet,
                    out bytes,
                    out string buildError,
                    externalSimulation,
                    simulationParameter,
                    _currentXrScenePack))
            {
                SetProcessViewerStatus("Live push skipped: " + buildError);
                return;
            }

            if (!canReusePackage)
            {
                _cachedLivePackageBytes = bytes;
                _cachedLivePackagePath = path;
                _cachedLivePackageContextIdentity = contextIdentity;
                _cachedLivePackageKpiSignature = kpiSignature;
            }

            _hasLiveStructureSnapshot = true;
            _lastLiveStructurePath = path;
            _lastLiveContextIdentity = contextIdentity;
            _lastLiveKpiSignature = kpiSignature;
            _lastLiveExternalSimulation = externalSimulation;
            _lastLiveSimulationParameter = simulationParameter;
            WasperLiveViewerClient client = _liveViewerClient;
            _ = PushLiveSafeAsync(client, bytes, externalSimulation, simulationParameter);
        }

        private async Task PushLiveSafeAsync(
            WasperLiveViewerClient client,
            byte[] bytes,
            bool externalSimulation,
            double simulationParameter)
        {
            try
            {
                // No ConfigureAwait(false) here, deliberately: the catch below touches WinForms
                // controls (via SetProcessViewerStatus), which is only safe on the UI thread.
                // Grasshopper/Rhino's main thread runs a WinForms message loop, so the default
                // await behavior (resume on the captured SynchronizationContext) lands this
                // continuation back there. WasperLiveViewerClient's own internal awaits use
                // ConfigureAwait(false) freely -- that's fine, this is the only frame in the
                // chain that needs the UI context preserved.
                await client.PushAsync(bytes, CancellationToken.None);
                // A cached structural package may contain an older sim_par. Correct it with the
                // tiny state message immediately after the package arrives; this also closes the
                // startup race where slider changes occurred while the server was booting.
                if (ReferenceEquals(client, _liveViewerClient))
                    QueueSimulationLiveUpdate(client, externalSimulation, simulationParameter);
            }
            catch (Exception exception)
            {
                // Routine and expected the first time (server not started/still booting) --
                // logged at status-line volume, not a GH runtime warning, since it self-heals
                // on the next successful push once the server catches up.
                SetProcessViewerStatus("Live push failed (will retry): " + exception.Message);
                if (ReferenceEquals(client, _liveViewerClient))
                {
                    _hasLiveStructureSnapshot = false;
                    MarkWebViewerServerUnavailable();
                }
            }
        }

        private void ScheduleDeferredLivePush()
        {
            if (_deferredLivePushScheduled)
                return;
            GH_Document document = OnPingDocument();
            if (document == null)
                return;

            double remaining = Math.Max(
                1.0,
                (LivePushMinInterval - (DateTime.UtcNow - _lastLivePushUtc)).TotalMilliseconds);
            _deferredLivePushScheduled = true;
            document.ScheduleSolution((int)Math.Ceiling(remaining), _ =>
            {
                _deferredLivePushScheduled = false;
                TryPushLiveUpdate();
            });
        }

        // Slider and future TCP updates are latest-state streams: if networking is briefly
        // slower than the producer, intermediate positions have no value. Keep at most one
        // pending value instead of allowing fire-and-forget tasks to queue seconds of stale
        // motion behind the ClientWebSocket send lock.
        private void QueueSimulationLiveUpdate(
            WasperLiveViewerClient client,
            bool externalSimulation,
            double simulationParameter)
        {
            lock (_liveSimulationQueueLock)
            {
                _pendingLiveExternalSimulation = externalSimulation;
                _pendingLiveSimulationParameter = simulationParameter;
                if (_liveSimulationSendInProgress)
                    return;
                _liveSimulationSendInProgress = true;
            }
            _ = DrainSimulationLiveUpdatesAsync(client);
        }

        private async Task DrainSimulationLiveUpdatesAsync(WasperLiveViewerClient client)
        {
            try
            {
                while (ReferenceEquals(client, _liveViewerClient))
                {
                    bool externalSimulation;
                    double value;
                    lock (_liveSimulationQueueLock)
                    {
                        if (!_pendingLiveSimulationParameter.HasValue)
                        {
                            _liveSimulationSendInProgress = false;
                            return;
                        }
                        externalSimulation = _pendingLiveExternalSimulation;
                        value = _pendingLiveSimulationParameter.Value;
                        _pendingLiveSimulationParameter = null;
                    }
                    await client.PushSimulationAsync(
                        externalSimulation,
                        value,
                        CancellationToken.None);
                }
            }
            catch (Exception exception)
            {
                SetProcessViewerStatus("Live simulation update failed (will retry): " + exception.Message);
                if (ReferenceEquals(client, _liveViewerClient))
                {
                    _hasLiveStructureSnapshot = false;
                    MarkWebViewerServerUnavailable();
                }
            }
            finally
            {
                lock (_liveSimulationQueueLock)
                {
                    _liveSimulationSendInProgress = false;
                    if (!ReferenceEquals(client, _liveViewerClient))
                        _pendingLiveSimulationParameter = null;
                }
            }
        }

        private void MarkWebViewerServerUnavailable()
        {
            if (_webViewerServerStartupInProgress)
                return;
            _webViewerServerStartRequested = false;
            _liveViewerClient?.Dispose();
            _liveViewerClient = null;
            _liveViewerClientSession = string.Empty;
            _hasLiveStructureSnapshot = false;
            EnsureWebViewerServerRunning();
        }

        private static string LiveKpiSignature(WasperKpiSet set)
        {
            if (set == null)
                return string.Empty;
            var builder = new StringBuilder();
            foreach (WasperKpi item in set.EnabledItems)
            {
                builder.Append(item.Key).Append('\u001f')
                    .Append(item.Label).Append('\u001f')
                    .Append(item.Group).Append('\u001f')
                    .Append(item.Method).Append('\u001f')
                    .Append(item.Value?.ToString("R", System.Globalization.CultureInfo.InvariantCulture))
                    .Append('\u001f').Append(item.TextValue).Append('\u001f')
                    .Append(item.Unit).Append('\u001f')
                    .Append(item.Description).Append('\u001f')
                    .Append(item.Source).Append('\u001e');
            }
            return builder.ToString();
        }

        // M5's closing deliverable, "Viewer status": polls /live/status so the Process Viewer
        // tab can show whether a browser is actually connected, rather than pushing live
        // updates into the void with no feedback. Same _liveEnabled gate and fire-and-forget
        // shape as TryPushLiveUpdate/PushLiveSafeAsync above, on its own slower interval since a
        // viewer count doesn't need to be as fresh as the job data itself.
        private void TryRefreshLiveViewerStatus(bool force = false)
        {
            if (!_liveEnabled)
                return;
            if (!force && DateTime.UtcNow - _lastLiveStatusPollUtc < LiveStatusPollMinInterval)
                return;
            _lastLiveStatusPollUtc = DateTime.UtcNow;
            _ = PollLiveViewerStatusSafeAsync();
        }

        private async Task PollLiveViewerStatusSafeAsync()
        {
            try
            {
                // No ConfigureAwait(false), same reasoning as PushLiveSafeAsync above: the
                // continuation touches WinForms controls via _form and must resume on the UI
                // thread's SynchronizationContext.
                string statusUrl = "http://localhost:5252/live/status?session=" +
                    Uri.EscapeDataString(LiveViewerSessionId());
                string json = await LiveStatusHttpClient.GetStringAsync(statusUrl);
                int viewerCount = ParseViewerCount(json);
                _form?.SetLiveViewerStatus(viewerCount switch
                {
                    0 => "No browser connected",
                    1 => "1 browser connected",
                    _ => $"{viewerCount} browsers connected"
                });
            }
            catch (Exception exception)
            {
                // Routine the first moment after "Open Web Viewer" is clicked (server still
                // booting) -- same self-healing story as PushLiveSafeAsync's own catch, the next
                // poll supersedes it once the server catches up.
                _form?.SetLiveViewerStatus("Status unavailable (server not reachable yet): " +
                    exception.Message);
                MarkWebViewerServerUnavailable();
            }
        }

        private static int ParseViewerCount(string statusJson)
        {
            JObject parsed = JObject.Parse(statusJson);
            return parsed["viewerCount"]?.Value<int>() ?? 0;
        }

        // Bundles the current job's already-exported .wasperxr plus, when one
        // exists, the whole study's study.json into one self-contained folder
        // via Package-StandaloneViewer.ps1 -- distinct from "External viewer
        // package" above, which only ever produces a single job. Launched as a
        // *visible* PowerShell window (unlike the hidden viewer-launch calls
        // elsewhere in this file): dotnet publish is a one-shot, potentially
        // 10-60+ second foreground build the user should be able to watch and
        // see errors from, and there is no safe way here to await it
        // synchronously and report completion back into the GH UI.
        private void BuildStandalonePackage(string folder, string name, bool zip)
        {
            string sourceFile = _lastProcessViewerJsonPath;
            if (!File.Exists(sourceFile))
            {
                SetDumpStudyStatus(
                    "Export a viewer package (above) before building a standalone study package.");
                return;
            }

            string normalizedFolder = string.IsNullOrWhiteSpace(folder)
                ? DefaultDumpStudyFolder()
                : folder.Trim();
            string normalizedName = string.IsNullOrWhiteSpace(name)
                ? DefaultDumpStudyName()
                : name.Trim();
            string outputFolder = Path.Combine(normalizedFolder, normalizedName);

            if (!TryResolveStandalonePackagerScript(out string scriptPath))
            {
                SetDumpStudyStatus(
                    "Standalone packager script was not found " +
                    "(04_WASPer_3DP.XR/07_WebViewer/scripts/Package-StandaloneViewer.ps1).");
                return;
            }

            string studyFile = DefaultStudyJsonPath();
            bool hasStudyFile = File.Exists(studyFile);
            WasperStudy studyForCoverage = _viewedStudy ?? _study;
            int totalIterations = studyForCoverage?.Iterations?.Count ?? 0;
            int iterationsWithXr = studyForCoverage?.Iterations?
                .Count(iteration => iteration.XrFiles != null && iteration.XrFiles.Count > 0) ?? 0;

            var arguments = new StringBuilder()
                .Append("-NoExit -NoProfile -ExecutionPolicy Bypass -File ")
                .Append(Quote(scriptPath))
                .Append(" -SourceFile ").Append(Quote(sourceFile))
                .Append(" -Name ").Append(Quote(normalizedName))
                .Append(" -OutputFolder ").Append(Quote(outputFolder));
            if (hasStudyFile)
                arguments.Append(" -StudyFile ").Append(Quote(studyFile));
            if (zip)
                arguments.Append(" -Zip");

            try
            {
                Directory.CreateDirectory(normalizedFolder);
                var startInfo = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = arguments.ToString(),
                    UseShellExecute = true,
                    WindowStyle = ProcessWindowStyle.Normal
                };
                Process.Start(startInfo);

                string coverageNote = !hasStudyFile
                    ? " No study.json was found -- this will be a single-job package with no Dashboard data."
                    : totalIterations > 0 && iterationsWithXr < totalIterations
                        ? $" Dashboard will cover {iterationsWithXr}/{totalIterations} study iterations " +
                          "(enable wsp_paths in the Run Study dialog to capture the rest)."
                        : string.Empty;
                SetDumpStudyStatus(
                    "Building standalone package in a PowerShell window -- dotnet publish can take a " +
                    "minute, watch it for progress or errors." + coverageNote + " Output: " + outputFolder);
            }
            catch (Exception exception)
            {
                SetDumpStudyStatus("Standalone package build failed: " + exception.Message);
            }
        }

        private void OpenDumpStudyFolder(string folder)
        {
            try
            {
                string target = string.IsNullOrWhiteSpace(folder)
                    ? DefaultDumpStudyFolder()
                    : folder;
                Directory.CreateDirectory(target);
                Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
            }
            catch (Exception exception)
            {
                SetDumpStudyStatus("Could not open folder: " + exception.Message);
            }
        }

        private void SetDumpStudyStatus(string status)
        {
            _dumpStudyStatus = status ?? string.Empty;
            _form?.SetDumpStudyResult(_dumpStudyStatus);
        }

        private string DefaultDumpStudyFolder()
        {
            string runName = _viewedStudy?.RunName ?? _study?.RunName ?? _currentFileName;
            return Path.Combine(
                ResolveStudyFolder(runName, _currentFilePath),
                "StandalonePackage");
        }

        private string DefaultDumpStudyName()
        {
            string runName = _viewedStudy?.RunName ?? _study?.RunName;
            return string.IsNullOrWhiteSpace(runName)
                ? ResolveBaseName(_currentFileName) + "_package"
                : runName;
        }

        private string DefaultStudyJsonPath()
        {
            string runName = _viewedStudy?.RunName ?? _study?.RunName ?? _currentFileName;
            return Path.Combine(ResolveStudyFolder(runName, _currentFilePath), "study.json");
        }

        private static bool TryResolveStandalonePackagerScript(out string scriptPath)
        {
            if (TryFindAncestorFile(
                    ViewerSearchAnchors(),
                    Path.Combine("webviewer", "scripts", "Package-StandaloneViewer.ps1"),
                    out scriptPath))
                return true;

            return TryFindAncestorFile(
                ViewerSearchAnchors(),
                Path.Combine("04_WASPer_3DP.XR", "07_WebViewer", "scripts", "Package-StandaloneViewer.ps1"),
                out scriptPath);
        }

        private void OpenProcessViewerFolder(string folder)
        {
            try
            {
                string target = string.IsNullOrWhiteSpace(folder)
                    ? DefaultProcessViewerFolder()
                    : folder;
                Directory.CreateDirectory(target);
                Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
            }
            catch (Exception exception)
            {
                SetProcessViewerStatus("Could not open folder: " + exception.Message);
            }
        }

        private void UpdateProcessViewerWindow()
        {
            WasperPrintPath path = _currentProcessViewerPath;
            bool hasPath = path?.HasPoints == true;
            bool hasMotionPlan = path?.HasMotionPlan == true;
            int branches = hasPath ? path.Points.BranchCount : 0;
            int motions = hasMotionPlan ? path.MotionPlan.Count : 0;
            string defaultFolder = DefaultProcessViewerFolder();
            string defaultJobId = DefaultProcessViewerJobId();
            string expectedPath = Path.Combine(
                defaultFolder,
                wsp_Gc07_Export_XR_Package.SanitizeJobId(defaultJobId) +
                    WasperXrBinaryPackage.Extension);
            string jsonPath = File.Exists(_lastProcessViewerJsonPath)
                ? _lastProcessViewerJsonPath
                : File.Exists(expectedPath)
                    ? expectedPath
                    : string.Empty;

            // Live synchronization belongs to Sm01, not to its optional WinForms window. The
            // previous placement below the form-null guard meant sim_par only reached the browser
            // while Study Manager was open; pressing Refresh appeared to fix it because opening
            // the form temporarily allowed this code to run. Keep the server, structural push,
            // lightweight playback stream, and status poll alive for every Sm01 solution.
            bool webRuntimeAvailable = TryCheckWebViewerRuntime(out string webRuntimeStatus);
            if (webRuntimeAvailable && (hasPath && hasMotionPlan || !string.IsNullOrWhiteSpace(jsonPath)))
                EnsureWebViewerServerRunning();
            if (webRuntimeAvailable)
            {
                TryPushLiveUpdate();
                TryRefreshLiveViewerStatus();
            }

            if (_form == null || _form.IsClosed)
                return;

            bool viewerAvailable = TryResolveProcessViewerFiles(out _, out _, out _);
            _form.UpdateProcessViewer(
                PreviewSampleName(),
                defaultFolder,
                defaultJobId,
                hasPath,
                hasMotionPlan,
                branches,
                motions,
                jsonPath,
                viewerAvailable,
                ProcessViewerAvailabilityMessage(),
                BuildViewerUrl(LocalViewerBaseUrl),
                webRuntimeAvailable,
                webRuntimeStatus);
            _form.SetProcessViewerResult(
                jsonPath,
                _processViewerStatus,
                viewerAvailable,
                webRuntimeAvailable,
                webRuntimeStatus);

            _form.UpdateDumpStudySection(
                DefaultDumpStudyFolder(),
                DefaultDumpStudyName(),
                File.Exists(_lastProcessViewerJsonPath));
            _form.SetDumpStudyResult(_dumpStudyStatus);
        }

        private void SetProcessViewerStatus(string status)
        {
            _processViewerStatus = status ?? string.Empty;
            _form?.SetProcessViewerResult(
                _lastProcessViewerJsonPath,
                _processViewerStatus,
                TryResolveProcessViewerFiles(out _, out _, out _),
                TryCheckWebViewerRuntime(out string webRuntimeStatus),
                webRuntimeStatus);
        }

        private bool TryCheckWebViewerRuntime(out string status)
        {
            if ((DateTime.UtcNow - _lastWebViewerRuntimeCheckUtc) < TimeSpan.FromSeconds(20))
            {
                status = _webViewerRuntimeStatus;
                return _webViewerRuntimeAvailable;
            }

            _lastWebViewerRuntimeCheckUtc = DateTime.UtcNow;
            _webViewerRuntimeAvailable = ProbeAspNetCoreRuntime8(out _webViewerRuntimeStatus);
            status = _webViewerRuntimeStatus;
            return _webViewerRuntimeAvailable;
        }

        private static bool ProbeAspNetCoreRuntime8(out string status)
        {
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = "dotnet",
                    Arguments = "--list-runtimes",
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using var process = Process.Start(startInfo);
                if (process == null)
                {
                    status = ".NET runtime check failed: dotnet could not be started.";
                    return false;
                }
                if (!process.WaitForExit(2500))
                {
                    try { process.Kill(); } catch { }
                    status = ".NET runtime check timed out.";
                    return false;
                }
                string output = process.StandardOutput.ReadToEnd();
                string error = process.StandardError.ReadToEnd();
                if (process.ExitCode != 0)
                {
                    status = "Missing .NET 8 ASP.NET Core Runtime. Install it from the Process Viewer guide.";
                    if (!string.IsNullOrWhiteSpace(error))
                        status += " dotnet: " + error.Trim();
                    return false;
                }

                foreach (string line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (line.StartsWith("Microsoft.AspNetCore.App 8.", StringComparison.OrdinalIgnoreCase))
                    {
                        status = "Web viewer runtime ready: " + line.Trim();
                        return true;
                    }
                }

                status = "Missing .NET 8 ASP.NET Core Runtime. Install it from the Process Viewer guide.";
                return false;
            }
            catch (Exception exception)
            {
                status = "Missing .NET 8 ASP.NET Core Runtime or dotnet host. Install it from the Process Viewer guide. " +
                    exception.Message;
                return false;
            }
        }

        private string DefaultProcessViewerFolder()
        {
            string runName = _viewedStudy?.RunName ?? _study?.RunName ?? _currentFileName;
            return Path.Combine(
                ResolveStudyFolder(runName, _currentFilePath),
                "XR");
        }

        private string DefaultProcessViewerJobId()
        {
            string sample = PreviewSampleName();
            return string.IsNullOrWhiteSpace(sample)
                ? ResolveBaseName(_currentFileName) + "_current"
                : sample;
        }

        private string LiveViewerSessionId()
        {
            Guid documentId = OnPingDocument()?.DocumentID ?? Guid.Empty;
            return documentId.ToString("N") + "-" + InstanceGuid.ToString("N");
        }

        private string LiveViewerSessionLabel()
        {
            string[] parts = LiveViewerSessionId().Split('-');
            return parts.Length == 2
                ? parts[0].Substring(0, 8) + "-" + parts[1].Substring(0, 8)
                : LiveViewerSessionId().Substring(0, 8);
        }

        private string BuildViewerUrl(string baseUrl, string jobPath = null)
        {
            string url = baseUrl + "?session=" + Uri.EscapeDataString(LiveViewerSessionId());
            if (!string.IsNullOrWhiteSpace(jobPath))
                url += "&path=" + Uri.EscapeDataString(jobPath);
            return url;
        }

        private static int NextProcessViewerRevision(string folder, string jobId)
        {
            string safeJobId = wsp_Gc07_Export_XR_Package.SanitizeJobId(jobId);
            if (string.IsNullOrWhiteSpace(safeJobId))
                safeJobId = "wasper-job";
            string path = Path.Combine(folder, safeJobId + WasperXrBinaryPackage.Extension);
            if (!File.Exists(path))
                return 1;
            return WasperXrBinaryPackage.ReadRevision(path) + 1;
        }

        // Anchor folders to start an ancestor walk-up from, in priority order.
        //
        // Assembly.GetExecutingAssembly().Location is the obvious first choice, but
        // this project builds with <EnableDynamicLoading>true</EnableDynamicLoading>
        // (WASPer_3DP.csproj) -- Rhino 8's hot-reload support for .gha plugins, which
        // loads the compiled assembly from an in-memory byte stream instead of
        // memory-mapping the file on disk. Under that load path .Location comes back
        // as an empty string at runtime, which silently short-circuited both viewer
        // lookups below (the walk-up loop's very first "folder != null" check failed,
        // so it never ran at all -- not a wrong path, no path).
        //
        // [CallerFilePath] is the fallback that actually works here: the compiler
        // bakes this source file's own absolute path into the IL as a string literal
        // at COMPILE time, so it is completely unaffected by how the assembly is
        // loaded at runtime. This file lives under 00_Visual_Studio, a sibling of
        // 04_WASPer_3DP.XR, so walking up from its own folder reaches the same place
        // the assembly-location walk-up was originally meant to reach. The only
        // requirement is that the source tree isn't moved after compiling, which is
        // true for this single-developer local build.
        private static IEnumerable<string> ViewerSearchAnchors(
            [CallerFilePath] string sourceFilePath = "")
        {
            string assemblyFolder = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            if (!string.IsNullOrWhiteSpace(assemblyFolder))
                yield return assemblyFolder;

            string sourceFolder = string.IsNullOrWhiteSpace(sourceFilePath)
                ? string.Empty
                : Path.GetDirectoryName(sourceFilePath);
            if (!string.IsNullOrWhiteSpace(sourceFolder))
                yield return sourceFolder;
        }

        private static bool TryFindAncestorFile(
            IEnumerable<string> startFolders,
            string relativePath,
            out string foundPath)
        {
            foreach (string startFolder in startFolders)
            {
                for (DirectoryInfo folder = new DirectoryInfo(startFolder);
                    folder != null;
                    folder = folder.Parent)
                {
                    string candidate = Path.Combine(folder.FullName, relativePath);
                    if (!File.Exists(candidate))
                        continue;
                    foundPath = candidate;
                    return true;
                }
            }
            foundPath = string.Empty;
            return false;
        }

        private static bool TryResolveProcessViewerFiles(
            out string gammaPath,
            out string patchPath,
            out string launcherPath)
        {
            gammaPath = @"C:\Program Files\vvvv\vvvv_gamma_7.4-win-x64\vvvv.exe";
            patchPath = string.Empty;
            launcherPath = string.Empty;
            if (!File.Exists(gammaPath))
                return false;

            // 04_WASPer_3DP.XR/02_ProcessViewer, not 05_VVVV/WASPerProcessViewer --
            // the latter was the pre-2026-08-18 location, moved during the XR
            // chapter restructure. This lookup is a runtime string path, not a
            // project reference, so the restructure's build-time checks never
            // caught it; it silently broke this button until fixed here.
            if (!TryFindAncestorFile(
                    ViewerSearchAnchors(),
                    Path.Combine("04_WASPer_3DP.XR", "02_ProcessViewer", "scripts", "Start-WASPerProcessViewer.ps1"),
                    out string candidateLauncher))
                return false;

            string viewerRoot = Path.GetDirectoryName(Path.GetDirectoryName(candidateLauncher));
            string candidatePatch = Path.Combine(viewerRoot ?? string.Empty, "WASPerProcessViewer.vl");
            patchPath = candidatePatch;
            launcherPath = candidateLauncher;
            return File.Exists(patchPath);
        }

        private static bool TryResolveWebViewerLauncher(out string launcherPath)
        {
            if (TryFindAncestorFile(
                    ViewerSearchAnchors(),
                    Path.Combine("webviewer", "scripts", "Start-WASPerWebViewer.ps1"),
                    out launcherPath))
                return true;

            return TryFindAncestorFile(
                ViewerSearchAnchors(),
                Path.Combine("04_WASPer_3DP.XR", "07_WebViewer", "scripts", "Start-WASPerWebViewer.ps1"),
                out launcherPath);
        }

        private static string ProcessViewerAvailabilityMessage()
        {
            string gamma = @"C:\Program Files\vvvv\vvvv_gamma_7.4-win-x64\vvvv.exe";
            if (!File.Exists(gamma))
                return "vvvv gamma 7.4 was not found.";
            return "Viewer patch pending: WASPerProcessViewer.vl was not found.";
        }

        private static string Quote(string value) =>
            "\"" + (value ?? string.Empty).Replace("\"", "\\\"") + "\"";
    }
}
