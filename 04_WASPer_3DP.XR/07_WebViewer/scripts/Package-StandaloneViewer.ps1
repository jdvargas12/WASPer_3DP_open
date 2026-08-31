<#
.SYNOPSIS
  Publishes WASPer.XR.WebViewer as a framework-dependent folder with
  one specific .wasperxr job baked in as its default view.

.DESCRIPTION
  Produces a lightweight folder that opens straight to the given job -- no
  file path to type, no server to point at manually. The target machine must
  have the .NET 8 ASP.NET Core Runtime installed. Meant to be zipped and
  handed to someone, or copied to a USB stick / network share.

  This is a static snapshot, not a live connection -- that's M5's job
  (06_Transport / WASPer.XR.WebSocket), not this script's. Re-run this after
  a new export to refresh the bundle with updated geometry.

.PARAMETER SourceFile
  Path to the .wasperxr file to bundle -- binary schema 0.2.0 (what Gc07
  writes by default) or the legacy JSON 0.1.0 form. Auto-detected by content,
  not filename.

.PARAMETER Name
  A short name for this package. Used as the output folder name and, if
  -Zip is set, the zip file name.

.PARAMETER StudyFile
  Optional path to a study.json (written by Sm01's Cartesian study runner,
  same folder as the study's Gcodes/Snapshots/XR subfolders). If given, the
  packaged viewer's Dashboard button opens straight to this study's charts
  with no path to type, same convenience as the bundled job. Omit it for a
  single-job package with no Dashboard data.

.PARAMETER OutputFolder
  Where to publish to. Defaults to "dist\<Name>" next to this script
  (04_WASPer_3DP.XR\07_WebViewer\dist\<Name>) -- move it under 02_Published
  or wherever afterward if you'd rather it live there.

.PARAMETER Zip
  If set, also produces "dist\<Name>.zip" for easy sharing.

.EXAMPLE
  .\Package-StandaloneViewer.ps1 -SourceFile "C:\exports\wall-01.wasperxr" -Name "Wall-01-Demo" -Zip

.EXAMPLE
  .\Package-StandaloneViewer.ps1 -SourceFile "C:\...\XR\wall-01.wasperxr" -StudyFile "C:\...\study.json" -Name "Wall-01-Study" -Zip
#>
param(
    [Parameter(Mandatory = $true)]
    [string]$SourceFile,

    [Parameter(Mandatory = $true)]
    [string]$Name,

    [string]$StudyFile,

    [string]$OutputFolder,

    [switch]$Zip
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path $SourceFile)) {
    throw "Source file not found: $SourceFile"
}
if ($StudyFile -and -not (Test-Path $StudyFile)) {
    throw "Study file not found: $StudyFile"
}

$scriptRoot = $PSScriptRoot
$sourceProject = Join-Path $scriptRoot "..\WASPer.XR.WebViewer\WASPer.XR.WebViewer.csproj"
$bundledAppRoot = Join-Path $scriptRoot "..\app"

if (-not $OutputFolder) {
    $OutputFolder = Join-Path $scriptRoot "..\dist\$Name"
}

Write-Host "Preparing WASPer.XR.WebViewer package at:"
Write-Host "  $OutputFolder"
if (Test-Path $sourceProject) {
    $projectFile = Resolve-Path $sourceProject
    dotnet publish $projectFile.Path -c Release -r win-x64 --self-contained false -o $OutputFolder
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed with exit code $LASTEXITCODE"
    }
}
elseif (Test-Path (Join-Path $bundledAppRoot "WASPer.XR.WebViewer.exe")) {
    if (Test-Path $OutputFolder) {
        Remove-Item -LiteralPath $OutputFolder -Recurse -Force
    }
    New-Item -ItemType Directory -Path $OutputFolder -Force | Out-Null
    Copy-Item -Path (Join-Path $bundledAppRoot "*") -Destination $OutputFolder -Recurse -Force
}
else {
    throw "Could not find the WebViewer source project or bundled app runtime."
}

# The viewer's default job slot (see Program.cs's /api/job: falls back to
# this exact path when no ?path= is given). Renaming the source file into
# this slot is what makes the packaged exe open straight to the bundled job
# instead of the generic sample fixture, with zero code changes needed.
$sampleDataFolder = Join-Path $OutputFolder "SampleData"
New-Item -ItemType Directory -Path $sampleDataFolder -Force | Out-Null
$targetFile = Join-Path $sampleDataFolder "wasper-xr-sample.wasperxr.json"
Copy-Item -Path $SourceFile -Destination $targetFile -Force
Write-Host "Bundled job: $SourceFile -> $targetFile"
Write-Host "(The .json in that filename is cosmetic -- the reader auto-detects binary vs JSON by content, not extension.)"

# Same convention as the job above: Program.cs's /api/study falls back to
# SampleData\study.json when no ?path= is given, so a bundled study makes
# the packaged Dashboard button work with zero typing too.
if ($StudyFile) {
    $studyTarget = Join-Path $sampleDataFolder "study.json"
    Copy-Item -Path $StudyFile -Destination $studyTarget -Force
    Write-Host "Bundled study: $StudyFile -> $studyTarget"
}

$exePath = Join-Path $OutputFolder "WASPer.XR.WebViewer.exe"
if (-not (Test-Path $exePath)) {
    throw "Publish did not produce the expected executable at $exePath"
}

if ($Zip) {
    $zipPath = Join-Path $scriptRoot "..\dist\$Name.zip"
    if (Test-Path $zipPath) { Remove-Item $zipPath }
    Compress-Archive -Path "$OutputFolder\*" -DestinationPath $zipPath
    Write-Host "Zipped: $zipPath"
}

Write-Host ""
Write-Host "Done. To view: run $exePath"
Write-Host "It opens a browser to http://localhost:5252 automatically. Close its console window to stop the viewer."
Write-Host "Requires .NET 8 ASP.NET Core Runtime on the target machine: https://dotnet.microsoft.com/download/dotnet/8.0"
if ($StudyFile) {
    Write-Host "Click the Dashboard button in the viewer to see this study's charts."
}
