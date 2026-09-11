<#
.SYNOPSIS
    One-time USB deployment migration utility - NOT a shipping runtime component. Merges the two
    legacy per-tool config.json files already deployed on a technician USB into the single
    canonical unified config Phase 3/4 read from.

.DESCRIPTION
    Reads:
        <UsbRoot>\Elkjop Toolkit\Account Setup\config.json   (Account, Connection sections)
        <UsbRoot>\Elkjop Toolkit\Support Tool\config.json    (Debloater, Installer sections)
    and writes:
        <UsbRoot>\Elkjop Toolkit\config.json

    with all four sections (Account/Connection/Debloater/Installer) present, preserving the
    existing values exactly - it does not manufacture different defaults, and it never modifies
    or deletes the two legacy source files (they remain the live config source for the
    still-shipping Account Setup path through Phase 4A).

    Fails clearly (throws, no partial file written) if either source file is missing, unreadable,
    malformed, or missing an expected section, rather than silently producing an incomplete
    canonical config.

.PARAMETER UsbRoot
    Path to the root of the technician USB deployment (the folder containing "Elkjop Toolkit\").

.PARAMETER Force
    Overwrite an existing canonical config.json. Without this switch, an existing canonical file
    is left untouched and the script fails rather than overwriting it.

.EXAMPLE
    .\Migrate-UnifiedConfig.ps1 -UsbRoot "X:\"

.EXAMPLE
    .\Migrate-UnifiedConfig.ps1 -UsbRoot "X:\" -Force
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$UsbRoot,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'

$toolkitFolder = Join-Path $UsbRoot 'Elkjop Toolkit'
$accountSetupConfigPath = Join-Path $toolkitFolder 'Account Setup\config.json'
$supportToolConfigPath = Join-Path $toolkitFolder 'Support Tool\config.json'
$canonicalConfigPath = Join-Path $toolkitFolder 'config.json'

if (-not (Test-Path $accountSetupConfigPath)) {
    throw "Account Setup config not found at '$accountSetupConfigPath'."
}

if (-not (Test-Path $supportToolConfigPath)) {
    throw "Support Tool config not found at '$supportToolConfigPath'."
}

if ((Test-Path $canonicalConfigPath) -and -not $Force) {
    throw "Canonical config already exists at '$canonicalConfigPath'. Pass -Force to overwrite it."
}

try {
    $accountSetupJson = Get-Content $accountSetupConfigPath -Raw | ConvertFrom-Json
}
catch {
    throw "Failed to parse Account Setup config '$accountSetupConfigPath': $_"
}

try {
    $supportToolJson = Get-Content $supportToolConfigPath -Raw | ConvertFrom-Json
}
catch {
    throw "Failed to parse Support Tool config '$supportToolConfigPath': $_"
}

if (-not $accountSetupJson.PSObject.Properties['Account']) {
    throw "Account Setup config '$accountSetupConfigPath' is missing its 'Account' section."
}

if (-not $accountSetupJson.PSObject.Properties['Connection']) {
    throw "Account Setup config '$accountSetupConfigPath' is missing its 'Connection' section."
}

if (-not $supportToolJson.PSObject.Properties['Debloater']) {
    throw "Support Tool config '$supportToolConfigPath' is missing its 'Debloater' section."
}

if (-not $supportToolJson.PSObject.Properties['Installer']) {
    throw "Support Tool config '$supportToolConfigPath' is missing its 'Installer' section."
}

# Straight structural merge - the existing per-section values are carried over exactly as they
# are; nothing here re-derives or defaults any of them.
$unified = [ordered]@{
    Account    = $accountSetupJson.Account
    Connection = $accountSetupJson.Connection
    Debloater  = $supportToolJson.Debloater
    Installer  = $supportToolJson.Installer
}

# -Depth well beyond the default (2) - Installer.Categories nests a dictionary of arrays of
# objects, which the default depth would silently truncate.
$json = $unified | ConvertTo-Json -Depth 20

Set-Content -Path $canonicalConfigPath -Value $json -Encoding utf8

Write-Host "Wrote canonical unified config to '$canonicalConfigPath'." -ForegroundColor Green
Write-Host "Legacy files were not modified:" -ForegroundColor DarkGray
Write-Host "  '$accountSetupConfigPath'" -ForegroundColor DarkGray
Write-Host "  '$supportToolConfigPath'" -ForegroundColor DarkGray
