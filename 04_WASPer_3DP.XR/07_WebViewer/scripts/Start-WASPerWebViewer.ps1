<#
.SYNOPSIS
  Starts (or reuses) the WASPer.XR.WebViewer local server and waits until
  it's actually accepting requests.

.DESCRIPTION
  Called from Sm01's Process Viewer tab -- originally from the "Open Web
  Viewer" button's click handler, now from EnsureWebViewerServerRunning,
  fired automatically from UpdateProcessViewerWindow as soon as there's
  something worth viewing (2026-08-19 rework: no button click required to
  start the server any more; "Open in Browser" is now a separate, purely
  local convenience action, see OpenWebViewerInBrowser). Mirrors
  Start-WASPerProcessViewer.ps1's role for the vvvv Gamma path, but for the
  browser-based viewer (M2/M3) instead of the parked vvvv one (M0). No
  external application or licence dependency: just a local ASP.NET Core
  server.

  If the viewer isn't already listening on port 5252, starts it via
  `dotnet run` in the background (no console window) and waits for it to
  come up, so the first thing that needs it doesn't just fail against a
  server that hasn't started yet. WASPER_VIEWER_NO_AUTOLAUNCH is set for
  that child process so Program.cs's own auto-open (meant for a
  double-clicked standalone package, see Package-StandaloneViewer.ps1)
  doesn't also pop a browser tab -- opening the tab is the caller's job now,
  not this script's.

  Opening the browser tab itself used to happen here too (a Start-Process
  call at the end), but moved to the C# caller (2026-08-19,
  WASPer_Sm01ProcessViewerController.cs's MonitorWebViewerServerStartupAsync,
  later split further into OpenWebViewerInBrowser once the server-start and
  browser-open actions were decoupled the same day): once this script
  started running fully hidden with its stdout/stderr
  redirected for error capture, that nested Start-Process/ShellExecute call
  silently stopped producing a visible browser window. Opening it directly
  from Grasshopper's own interactive process instead (the same pattern
  already used for Explorer via OpenProcessViewerFolder) sidesteps the
  whole hidden-child-process chain rather than chasing exactly why
  ShellExecute stopped working from in here. This script's only job now is
  guaranteeing the server is actually reachable before the caller opens
  anything.
#>

$ErrorActionPreference = "Stop"
$baseUrl = "http://localhost:5252"
$scriptRoot = $PSScriptRoot
$bundledAppRoot = Join-Path $scriptRoot "..\app"
$bundledExe = Join-Path $bundledAppRoot "WASPer.XR.WebViewer.exe"
$bundledDll = Join-Path $bundledAppRoot "WASPer.XR.WebViewer.dll"
$sourceProject = Join-Path $scriptRoot "..\WASPer.XR.WebViewer\WASPer.XR.WebViewer.csproj"
$launchMutex = New-Object System.Threading.Mutex($false, "Local\WASPer.XR.WebViewer.Launch")
$ownsLaunchMutex = $false
$runtimeLogRoot = Join-Path $env:LOCALAPPDATA "WASPer_3DP\WebViewer\logs"
New-Item -ItemType Directory -Path $runtimeLogRoot -Force | Out-Null
$launchStamp = Get-Date -Format "yyyyMMdd-HHmmss-fff"
$viewerStdOut = Join-Path $runtimeLogRoot "viewer-$launchStamp.out.log"
$viewerStdErr = Join-Path $runtimeLogRoot "viewer-$launchStamp.err.log"

function Test-AspNetCoreRuntime8 {
    try {
        $runtimes = & dotnet --list-runtimes 2>$null
        if ($LASTEXITCODE -ne 0) {
            return $false
        }
        return ($runtimes | Where-Object { $_ -match '^Microsoft\.AspNetCore\.App\s+8\.' }).Count -gt 0
    }
    catch {
        return $false
    }
}

function Test-ViewerRunning {
    try {
        $response = Invoke-WebRequest -Uri $baseUrl -UseBasicParsing -TimeoutSec 2
        return $response.StatusCode -ge 200
    }
    catch {
        return $false
    }
}

try {
    # Several Sm01 callbacks can discover an unavailable endpoint at nearly the same time
    # (status poll, live push, Refresh, or reopening the manager). Serialize the complete
    # probe/start sequence across every component and PowerShell process so only one viewer can
    # ever race for port 5252.
    $ownsLaunchMutex = $launchMutex.WaitOne([TimeSpan]::FromSeconds(125))
    if (-not $ownsLaunchMutex) {
        throw "Timed out waiting for another WASPer WebViewer launch attempt to finish."
    }

    if (-not (Test-ViewerRunning)) {
        # A failed Kestrel bind can leave an error-reporting/stalled process visible in Task
        # Manager even though it owns no listening socket. Such a process fooled later recovery
        # attempts into repeatedly launching competitors. If the HTTP endpoint is definitely
        # unavailable, clear only WASPer viewer processes before starting the single replacement.
        Get-Process -Name "WASPer.XR.WebViewer" -ErrorAction SilentlyContinue |
            Stop-Process -Force -ErrorAction SilentlyContinue
        Start-Sleep -Milliseconds 300

        Write-Host "Starting WASPer.XR.WebViewer..."
        $env:WASPER_VIEWER_NO_AUTOLAUNCH = "1"
        if (Test-Path $bundledExe) {
            if (-not (Test-AspNetCoreRuntime8)) {
                throw "Missing .NET 8 ASP.NET Core Runtime. Install it from https://dotnet.microsoft.com/download/dotnet/8.0, then reopen Rhino/Grasshopper and try again."
            }
            Start-Process -FilePath $bundledExe `
                -WorkingDirectory $bundledAppRoot `
                -RedirectStandardOutput $viewerStdOut `
                -RedirectStandardError $viewerStdErr `
                -WindowStyle Hidden
        }
        elseif (Test-Path $bundledDll) {
            if (-not (Test-AspNetCoreRuntime8)) {
                throw "Missing .NET 8 ASP.NET Core Runtime. Install it from https://dotnet.microsoft.com/download/dotnet/8.0, then reopen Rhino/Grasshopper and try again."
            }
            Start-Process -FilePath "dotnet" `
                -ArgumentList "`"$bundledDll`"" `
                -WorkingDirectory $bundledAppRoot `
                -RedirectStandardOutput $viewerStdOut `
                -RedirectStandardError $viewerStdErr `
                -WindowStyle Hidden
        }
        else {
            $projectFile = Resolve-Path $sourceProject
            Start-Process -FilePath "dotnet" `
                -ArgumentList "run --project `"$($projectFile.Path)`" -c Release" `
                -RedirectStandardOutput $viewerStdOut `
                -RedirectStandardError $viewerStdErr `
                -WindowStyle Hidden
        }

        # 120s, not 30s (2026-08-19): a cold `dotnet run` -- first launch after any source change,
        # since that forces a real rebuild rather than reusing cached bin/obj output -- genuinely
        # took longer than 30 seconds on this machine and got mistaken for a hung/failed launch. A
        # warm run (no code changes since the last build) still comes up in a couple of seconds, so
        # this only matters for that first click after editing anything.
        $elapsed = 0
        $timeoutMs = 120000
        while (-not (Test-ViewerRunning)) {
            Start-Sleep -Milliseconds 500
            $elapsed += 500
            if ($elapsed % 10000 -eq 0) {
                Write-Host "Still waiting for WASPer.XR.WebViewer to start... ($($elapsed / 1000)s elapsed)"
            }
            if ($elapsed -ge $timeoutMs) {
                throw "WASPer.XR.WebViewer did not start listening on $baseUrl within $($timeoutMs / 1000) seconds."
            }
        }
        Write-Host "Viewer is up."
    }
    else {
        Write-Host "WASPer.XR.WebViewer is already running -- reusing it."
    }
}
finally {
    if ($ownsLaunchMutex) {
        $launchMutex.ReleaseMutex()
    }
    $launchMutex.Dispose()
}
