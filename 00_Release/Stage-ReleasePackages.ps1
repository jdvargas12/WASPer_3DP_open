param(
    [string]$Configuration = "Release",
    [string]$Framework = "net8.0-windows",
    [string]$PublishedRoot = "",
    [switch]$SkipYak
)

$ErrorActionPreference = "Stop"

function Assert-UnderRoot {
    param(
        [string]$Path,
        [string]$Root,
        [string]$Label
    )

    $fullPath = [System.IO.Path]::GetFullPath($Path)
    $fullRoot = [System.IO.Path]::GetFullPath($Root).TrimEnd('\', '/')
    $rootPrefix = $fullRoot + [System.IO.Path]::DirectorySeparatorChar

    if (-not $fullPath.Equals($fullRoot, [System.StringComparison]::OrdinalIgnoreCase) -and
        -not $fullPath.StartsWith($rootPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "$Label is outside the expected root: $fullPath"
    }
}

function Reset-Directory {
    param(
        [string]$Path,
        [string]$AllowedRoot
    )

    Assert-UnderRoot -Path $Path -Root $AllowedRoot -Label "Release package directory"
    if (Test-Path -LiteralPath $Path) {
        Remove-Item -LiteralPath $Path -Recurse -Force
    }
    New-Item -ItemType Directory -Force -Path $Path | Out-Null
}

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$visualStudioRoot = Resolve-Path (Join-Path $scriptRoot "..")
$wipRoot = Resolve-Path (Join-Path $visualStudioRoot "..")
$projectRoot = Resolve-Path (Join-Path $visualStudioRoot "01_WASPer_3DP")
$sourceGhuser = Resolve-Path (Join-Path $projectRoot "Resources\Ghuser")
$buildOutput = Resolve-Path (Join-Path $projectRoot "bin\$Configuration\$Framework")

if ([string]::IsNullOrWhiteSpace($PublishedRoot)) {
    $PublishedRoot = $env:WASPER_PUBLISHED_ROOT
}
if ([string]::IsNullOrWhiteSpace($PublishedRoot)) {
    $settingsPath = Join-Path (Join-Path $env:LOCALAPPDATA "WASPer_3DP") "release-settings.json"
    if (Test-Path -LiteralPath $settingsPath) {
        try {
            $settings = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json
            $PublishedRoot = [string]$settings.publishedRoot
        }
        catch {
            $PublishedRoot = ""
        }
    }
}
if ([string]::IsNullOrWhiteSpace($PublishedRoot)) {
    $PublishedRoot = Join-Path (Split-Path -Parent $buildOutput) "Published"
}
$PublishedRoot = [System.IO.Path]::GetFullPath($PublishedRoot)
New-Item -ItemType Directory -Force -Path $PublishedRoot | Out-Null

$manifestPath = Join-Path $buildOutput "manifest.yml"
if (-not (Test-Path -LiteralPath $manifestPath)) {
    throw "Build output is missing manifest.yml: $manifestPath"
}
foreach ($requiredFile in @("WASPer_3DP.gha", "WASPer_3DP.Robots.gha")) {
    if (-not (Test-Path -LiteralPath (Join-Path $buildOutput $requiredFile))) {
        throw "Release build is incomplete. Missing $requiredFile in $buildOutput"
    }
}

$versionMatch = Select-String -LiteralPath $manifestPath -Pattern '^version:\s*([0-9]+(?:\.[0-9]+){3})\s*$' | Select-Object -First 1
if ($null -eq $versionMatch) {
    throw "Could not read a four-part version from $manifestPath"
}

$version = $versionMatch.Matches[0].Groups[1].Value
$versionParts = $version.Split('.')
$versionSeries = "v$($versionParts[0]).$($versionParts[1]).$($versionParts[2])"
$compactVersion = "v" + ($version -replace '\.', '')
$buildDate = Get-Date -Format "yyMMdd"
$frameworkLabel = if ($Framework -match '^(net[0-9]+\.[0-9]+)') { $Matches[1] } else { $Framework -replace '-windows$', '' }

$versionRoot = Join-Path $PublishedRoot $versionSeries
$packageManagerRoot = Join-Path $versionRoot "${compactVersion}_${buildDate}_${frameworkLabel}"
$food4RhinoRoot = Join-Path $versionRoot "${compactVersion}_${buildDate}_f4f"
$food4RhinoLib = Join-Path $food4RhinoRoot "WASPer_3DP_lib"
$food4RhinoGhuser = Join-Path $food4RhinoRoot "WASPer_3DP_ghuser"

New-Item -ItemType Directory -Force -Path $versionRoot | Out-Null
Reset-Directory -Path $packageManagerRoot -AllowedRoot $versionRoot
Reset-Directory -Path $food4RhinoRoot -AllowedRoot $versionRoot
New-Item -ItemType Directory -Force -Path $food4RhinoLib, $food4RhinoGhuser | Out-Null

# Package Manager: preserve the compiled output layout and regenerate the Yak archive.
Get-ChildItem -LiteralPath $buildOutput -Force |
    Where-Object { $_.Name -ne "obj" -and $_.Extension -ne ".yak" } |
    Copy-Item -Destination $packageManagerRoot -Recurse -Force

$packageGhuser = Join-Path $packageManagerRoot "ghuser"
if (Test-Path -LiteralPath $packageGhuser) {
    Remove-Item -LiteralPath $packageGhuser -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $packageGhuser | Out-Null
Get-ChildItem -LiteralPath $sourceGhuser -Filter "*.ghuser" -File |
    Sort-Object Name |
    Copy-Item -Destination $packageGhuser -Force

$yakPackage = $null
if (-not $SkipYak) {
    $yakCommand = Get-Command yak -ErrorAction SilentlyContinue
    $yakPath = if ($yakCommand) { $yakCommand.Source } else { Join-Path $env:ProgramFiles "Rhino 8\System\yak.exe" }
    if (Test-Path -LiteralPath $yakPath) {
        Push-Location $packageManagerRoot
        try {
            & $yakPath build --platform any
            if ($LASTEXITCODE -ne 0) {
                throw "Yak build failed with exit code $LASTEXITCODE"
            }
        }
        finally {
            Pop-Location
        }
        $yakPackage = Get-ChildItem -LiteralPath $packageManagerRoot -Filter "*.yak" -File | Select-Object -First 1
    }
    else {
        Write-Warning "Yak was not found. The Package Manager folder was staged without a .yak archive."
    }
}

# food4Rhino: libraries/resources and user objects remain in separate install folders.
Get-ChildItem -LiteralPath $buildOutput -Force |
    Where-Object { $_.Name -notin @("ghuser", "manifest.yml", "obj") -and $_.Extension -ne ".yak" } |
    Copy-Item -Destination $food4RhinoLib -Recurse -Force
Get-ChildItem -LiteralPath $sourceGhuser -Filter "*.ghuser" -File |
    Sort-Object Name |
    Copy-Item -Destination $food4RhinoGhuser -Force

[pscustomobject]@{
    Version = $version
    PackageManager = $packageManagerRoot
    YakPackage = if ($yakPackage) { $yakPackage.FullName } else { $null }
    Food4Rhino = $food4RhinoRoot
    ActiveGhuserFiles = (Get-ChildItem -LiteralPath $sourceGhuser -Filter "*.ghuser" -File).Count
}
