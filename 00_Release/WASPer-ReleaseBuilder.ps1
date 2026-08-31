param(
    [string]$Version = "",
    [string]$PublishedRoot = "",
    [switch]$NonInteractive,
    [switch]$NoBuild,
    [switch]$NoSaveSettings
)

$ErrorActionPreference = "Stop"

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$visualStudioRoot = [System.IO.Path]::GetFullPath((Join-Path $scriptRoot ".."))
$mainProjectRoot = Join-Path $visualStudioRoot "01_WASPer_3DP"
$manifestHeader = Join-Path $mainProjectRoot "Components\0.0_WASPer_3DP\manifest.header.yml"
$robotsProject = Join-Path $visualStudioRoot "03_WASPer_3DP.Robots\WASPer_3DP.Robots.csproj"
$defaultPublishedRoot = Join-Path $mainProjectRoot "bin\Release\Published"
$settingsDirectory = Join-Path $env:LOCALAPPDATA "WASPer_3DP"
$settingsPath = Join-Path $settingsDirectory "release-settings.json"
$versionPattern = '^[0-9]+(?:\.[0-9]+){3}$'

function Read-CurrentVersion {
    $text = [System.IO.File]::ReadAllText($manifestHeader)
    $match = [System.Text.RegularExpressions.Regex]::Match(
        $text,
        '(?m)^version:\s*([0-9]+(?:\.[0-9]+){3})\s*$')
    if (-not $match.Success) {
        throw "Could not read a four-part version from $manifestHeader"
    }
    return $match.Groups[1].Value
}

function Read-SavedPublishedRoot {
    if (-not (Test-Path -LiteralPath $settingsPath)) {
        return ""
    }
    try {
        $settings = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json
        return [string]$settings.publishedRoot
    }
    catch {
        return ""
    }
}

function Save-PublishedRoot([string]$Path) {
    New-Item -ItemType Directory -Force -Path $settingsDirectory | Out-Null
    @{ publishedRoot = $Path } |
        ConvertTo-Json |
        Set-Content -LiteralPath $settingsPath -Encoding UTF8
}

function Set-ReleaseVersion([string]$NewVersion) {
    if ((Read-CurrentVersion) -eq $NewVersion) {
        return
    }
    $text = [System.IO.File]::ReadAllText($manifestHeader)
    $updated = [System.Text.RegularExpressions.Regex]::Replace(
        $text,
        '(?m)^version:\s*[0-9]+(?:\.[0-9]+){3}\s*$',
        "version: $NewVersion",
        1)
    if ($updated -eq $text -and (Read-CurrentVersion) -ne $NewVersion) {
        throw "Could not update the version in $manifestHeader"
    }
    $utf8Bom = New-Object System.Text.UTF8Encoding($true)
    [System.IO.File]::WriteAllText($manifestHeader, $updated, $utf8Bom)
}

function Show-ReleaseDialog([string]$CurrentVersion, [string]$InitialRoot) {
    Add-Type -AssemblyName System.Windows.Forms
    Add-Type -AssemblyName System.Drawing

    $form = New-Object System.Windows.Forms.Form
    $form.Text = "WASPer Release Builder"
    $form.StartPosition = "CenterScreen"
    $form.FormBorderStyle = "FixedDialog"
    $form.MaximizeBox = $false
    $form.MinimizeBox = $false
    $form.ClientSize = New-Object System.Drawing.Size(620, 220)
    $form.Font = New-Object System.Drawing.Font("Segoe UI", 10)

    $versionLabel = New-Object System.Windows.Forms.Label
    $versionLabel.Text = "Release version"
    $versionLabel.Location = New-Object System.Drawing.Point(20, 20)
    $versionLabel.AutoSize = $true
    $form.Controls.Add($versionLabel)

    $versionBox = New-Object System.Windows.Forms.TextBox
    $versionBox.Text = $CurrentVersion
    $versionBox.Location = New-Object System.Drawing.Point(20, 45)
    $versionBox.Size = New-Object System.Drawing.Size(180, 28)
    $form.Controls.Add($versionBox)

    $pathLabel = New-Object System.Windows.Forms.Label
    $pathLabel.Text = "Publication folder (saved only on this computer)"
    $pathLabel.Location = New-Object System.Drawing.Point(20, 88)
    $pathLabel.AutoSize = $true
    $form.Controls.Add($pathLabel)

    $pathBox = New-Object System.Windows.Forms.TextBox
    $pathBox.Text = $InitialRoot
    $pathBox.Location = New-Object System.Drawing.Point(20, 113)
    $pathBox.Size = New-Object System.Drawing.Size(485, 28)
    $form.Controls.Add($pathBox)

    $browseButton = New-Object System.Windows.Forms.Button
    $browseButton.Text = "Browse..."
    $browseButton.Location = New-Object System.Drawing.Point(515, 111)
    $browseButton.Size = New-Object System.Drawing.Size(85, 31)
    $browseButton.Add_Click({
        $picker = New-Object System.Windows.Forms.FolderBrowserDialog
        $picker.Description = "Choose where WASPer release folders should be created"
        $picker.SelectedPath = $pathBox.Text
        if ($picker.ShowDialog($form) -eq [System.Windows.Forms.DialogResult]::OK) {
            $pathBox.Text = $picker.SelectedPath
        }
        $picker.Dispose()
    })
    $form.Controls.Add($browseButton)

    $cancelButton = New-Object System.Windows.Forms.Button
    $cancelButton.Text = "Cancel"
    $cancelButton.DialogResult = [System.Windows.Forms.DialogResult]::Cancel
    $cancelButton.Location = New-Object System.Drawing.Point(405, 170)
    $cancelButton.Size = New-Object System.Drawing.Size(90, 32)
    $form.Controls.Add($cancelButton)

    $buildButton = New-Object System.Windows.Forms.Button
    $buildButton.Text = "Build Release"
    $buildButton.Location = New-Object System.Drawing.Point(505, 170)
    $buildButton.Size = New-Object System.Drawing.Size(95, 32)
    $buildButton.Add_Click({
        if ($versionBox.Text.Trim() -notmatch $versionPattern) {
            [System.Windows.Forms.MessageBox]::Show(
                $form,
                "Enter a four-part version such as 1.0.5.9.",
                "Invalid version",
                [System.Windows.Forms.MessageBoxButtons]::OK,
                [System.Windows.Forms.MessageBoxIcon]::Warning) | Out-Null
            return
        }
        if ([string]::IsNullOrWhiteSpace($pathBox.Text)) {
            [System.Windows.Forms.MessageBox]::Show(
                $form,
                "Choose a publication folder.",
                "Missing folder",
                [System.Windows.Forms.MessageBoxButtons]::OK,
                [System.Windows.Forms.MessageBoxIcon]::Warning) | Out-Null
            return
        }
        $form.Tag = @{
            Version = $versionBox.Text.Trim()
            PublishedRoot = [System.IO.Path]::GetFullPath($pathBox.Text.Trim())
        }
        $form.DialogResult = [System.Windows.Forms.DialogResult]::OK
        $form.Close()
    })
    $form.Controls.Add($buildButton)

    $form.AcceptButton = $buildButton
    $form.CancelButton = $cancelButton
    $result = $form.ShowDialog()
    $selection = $form.Tag
    $form.Dispose()
    if ($result -ne [System.Windows.Forms.DialogResult]::OK) {
        return $null
    }
    return $selection
}

$currentVersion = Read-CurrentVersion
$savedRoot = Read-SavedPublishedRoot
if ([string]::IsNullOrWhiteSpace($savedRoot)) {
    $savedRoot = $defaultPublishedRoot
}

if ($NonInteractive) {
    if ([string]::IsNullOrWhiteSpace($Version)) { $Version = $currentVersion }
    if ([string]::IsNullOrWhiteSpace($PublishedRoot)) { $PublishedRoot = $savedRoot }
}
else {
    $selection = Show-ReleaseDialog -CurrentVersion $currentVersion -InitialRoot $savedRoot
    if ($null -eq $selection) {
        Write-Host "WASPer release cancelled."
        exit 0
    }
    $Version = $selection.Version
    $PublishedRoot = $selection.PublishedRoot
}

if ($Version -notmatch $versionPattern) {
    throw "Invalid release version '$Version'. Expected four parts, for example 1.0.5.9."
}
$PublishedRoot = [System.IO.Path]::GetFullPath($PublishedRoot)

Set-ReleaseVersion -NewVersion $Version
if (-not $NoSaveSettings) {
    Save-PublishedRoot -Path $PublishedRoot
}

if ($NoBuild) {
    [pscustomobject]@{
        Version = $Version
        PublishedRoot = $PublishedRoot
        SettingsPath = if ($NoSaveSettings) { $null } else { $settingsPath }
        BuildStarted = $false
    }
    exit 0
}

Write-Host ""
Write-Host "Building WASPer_3DP $Version" -ForegroundColor Cyan
Write-Host "Publication root: $PublishedRoot"
Write-Host ""

$previousPublishedRoot = $env:WASPER_PUBLISHED_ROOT
$env:WASPER_PUBLISHED_ROOT = $PublishedRoot
try {
    & dotnet build $robotsProject -c Release
    if ($LASTEXITCODE -ne 0) {
        throw "Release build failed with exit code $LASTEXITCODE."
    }
}
finally {
    $env:WASPER_PUBLISHED_ROOT = $previousPublishedRoot
}

Write-Host ""
Write-Host "WASPer release completed." -ForegroundColor Green
Write-Host "Packages: $PublishedRoot"

if (-not $NonInteractive) {
    Add-Type -AssemblyName System.Windows.Forms
    [System.Windows.Forms.MessageBox]::Show(
        "Release $Version completed.`r`n`r`n$PublishedRoot",
        "WASPer Release Builder",
        [System.Windows.Forms.MessageBoxButtons]::OK,
        [System.Windows.Forms.MessageBoxIcon]::Information) | Out-Null
}
