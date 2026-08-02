# Copyright (C) 2026 Touché PX5 Project
# SPDX-License-Identifier: GPL-2.0-or-later

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $GamePath,

    [string] $LauncherDirectory,

    [ValidateSet('Trace', 'Debug', 'Info', 'Warning', 'Error', 'Critical')]
    [string] $LogLevel,

    [string] $LogFile,

    [string[]] $DiagnosticToggle = @(),

    [string[]] $AdditionalArgument = @(),

    [switch] $DryRun
)

$ErrorActionPreference = 'Stop'

function Set-ToucheEnvironmentVariable {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Name,

        [Parameter(Mandatory = $true)]
        [string] $Value
    )

    $publicName = if ($Name.StartsWith('SHARPEMU_', [System.StringComparison]::OrdinalIgnoreCase)) {
        'TOUCHEPX5_' + $Name.Substring('SHARPEMU_'.Length)
    }
    else {
        $Name
    }

    if (-not $publicName.StartsWith('TOUCHEPX5_', [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Invalid diagnostic toggle '$Name'. Expected a TOUCHEPX5_ or SHARPEMU_ variable."
    }

    Set-Item -LiteralPath "env:$publicName" -Value $Value
    $compatibilityName = 'SHARPEMU_' + $publicName.Substring('TOUCHEPX5_'.Length)
    Set-Item -LiteralPath "env:$compatibilityName" -Value $Value
}

$repositoryRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($LauncherDirectory)) {
    $LauncherDirectory = Join-Path $repositoryRoot 'artifacts\publish\firmware-support\win-x64'
}

$LauncherDirectory = [System.IO.Path]::GetFullPath($LauncherDirectory)
$settingsPath = Join-Path $LauncherDirectory 'gui-settings.json'
if (-not (Test-Path -LiteralPath $settingsPath -PathType Leaf)) {
    throw "Launcher settings were not found: $settingsPath"
}

if (-not (Test-Path -LiteralPath $GamePath -PathType Leaf)) {
    throw "Game executable was not found: $GamePath"
}

$settings = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json
$emulatorPath = if (
    -not [string]::IsNullOrWhiteSpace($settings.EmulatorPath) -and
    (Test-Path -LiteralPath $settings.EmulatorPath -PathType Leaf)
) {
    [System.IO.Path]::GetFullPath([string] $settings.EmulatorPath)
}
else {
    Join-Path $LauncherDirectory 'TouchePx5.exe'
}

if (-not (Test-Path -LiteralPath $emulatorPath -PathType Leaf)) {
    throw "Emulator executable was not found: $emulatorPath"
}

$effectiveLogLevel = if ([string]::IsNullOrWhiteSpace($LogLevel)) {
    if ([string]::IsNullOrWhiteSpace($settings.LogLevel)) { 'Info' } else { [string] $settings.LogLevel }
}
else {
    $LogLevel
}

if ([string]::IsNullOrWhiteSpace($LogFile)) {
    $logDirectory = Join-Path $repositoryRoot 'logs\automated'
    $gameName = [System.IO.Path]::GetFileNameWithoutExtension($GamePath)
    $safeGameName = $gameName -replace '[^A-Za-z0-9._-]', '_'
    $LogFile = Join-Path $logDirectory ("{0}-{1:yyyyMMdd-HHmmss}.log" -f $safeGameName, (Get-Date))
}

$LogFile = [System.IO.Path]::GetFullPath($LogFile)
$logParent = Split-Path -Parent $LogFile
if (-not $DryRun) {
    New-Item -ItemType Directory -Path $logParent -Force | Out-Null
}

foreach ($toggle in @($settings.EnvironmentToggles)) {
    if (-not [string]::IsNullOrWhiteSpace($toggle)) {
        Set-ToucheEnvironmentVariable -Name ([string] $toggle) -Value '1'
    }
}

foreach ($toggle in $DiagnosticToggle) {
    if (-not [string]::IsNullOrWhiteSpace($toggle)) {
        Set-ToucheEnvironmentVariable -Name $toggle -Value '1'
    }
}

if ($null -ne $settings.RenderResolutionScale) {
    Set-ToucheEnvironmentVariable -Name 'TOUCHEPX5_RENDER_SCALE' -Value (
        [double] $settings.RenderResolutionScale
    ).ToString('0.###', [System.Globalization.CultureInfo]::InvariantCulture)
}

if (-not [string]::IsNullOrWhiteSpace($settings.VulkanDevice)) {
    Set-ToucheEnvironmentVariable -Name 'TOUCHEPX5_VK_DEVICE' -Value ([string] $settings.VulkanDevice)
}

$arguments = [System.Collections.Generic.List[string]]::new()
$arguments.Add('--cpu-engine=native')
$arguments.Add("--log-level=$effectiveLogLevel")
$arguments.Add("--log-file=$LogFile")

if ($settings.StrictDynlibResolution -eq $true) {
    $arguments.Add('--strict')
}

$traceLimit = 0
if ($null -ne $settings.ImportTraceLimit) {
    $traceLimit = [int] $settings.ImportTraceLimit
}
if ($traceLimit -gt 0) {
    $arguments.Add("--trace-imports=$traceLimit")
}

$firmwareStore = Join-Path $LauncherDirectory 'user\firmware-profiles'
$profileId = [string] $settings.ActiveFirmwareProfileId
if ($settings.EnableExperimentalFirmwareLle -eq $true -and -not [string]::IsNullOrWhiteSpace($profileId)) {
    $profileManifest = Join-Path $firmwareStore "profiles\$profileId\manifest.json"
    if (-not (Test-Path -LiteralPath $profileManifest -PathType Leaf)) {
        throw "The configured firmware profile is missing: $profileId"
    }

    $arguments.Add('--firmware-lle')
    $arguments.Add("--firmware-store=$firmwareStore")
    $arguments.Add("--firmware-profile=$profileId")
}

foreach ($argument in $AdditionalArgument) {
    if (-not [string]::IsNullOrWhiteSpace($argument)) {
        $arguments.Add($argument)
    }
}
$arguments.Add([System.IO.Path]::GetFullPath($GamePath))

Write-Output "Emulator: $emulatorPath"
Write-Output "Settings: $settingsPath"
Write-Output "Firmware LLE: $($settings.EnableExperimentalFirmwareLle -eq $true)"
Write-Output "Firmware profile: $profileId"
Write-Output "GPU: $($settings.VulkanDevice)"
Write-Output "Log: $LogFile"
Write-Output ('Arguments: ' + ($arguments -join ' '))

if ($DryRun) {
    exit 0
}

Push-Location (Split-Path -Parent $emulatorPath)
try {
    & $emulatorPath @arguments
    exit $LASTEXITCODE
}
finally {
    Pop-Location
}
