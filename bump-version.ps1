#!/usr/bin/env pwsh
# Single source of truth for the plugin version is <Version> in BodyFlux/BodyFlux.csproj.
# This script bumps it and PROPAGATES the new version to every other file that needs it:
#   - pluginmaster.json : "AssemblyVersion" and the tag in the three DownloadLink* URLs
# (BodyFlux/BodyFlux.json has no version on purpose — DalamudPackager stamps AssemblyVersion
#  from the csproj <Version> at build time.)
#
#   ./bump-version.ps1            # build:  0.5.0.0 -> 0.5.0.1
#   ./bump-version.ps1 patch      # patch:  0.5.0.4 -> 0.5.1.0
#   ./bump-version.ps1 minor      # minor:  0.5.3.2 -> 0.6.0.0   (e.g. 0.5 -> 0.6)
#   ./bump-version.ps1 major      # major:  0.6.4.1 -> 1.0.0.0
#   ./bump-version.ps1 sync       # don't bump; just re-sync the other files to the csproj version
param([ValidateSet('build', 'patch', 'minor', 'major', 'sync')][string]$Part = 'build')

$ErrorActionPreference = 'Stop'
$csproj       = Join-Path $PSScriptRoot 'BodyFlux/BodyFlux.csproj'
$pluginmaster = Join-Path $PSScriptRoot 'pluginmaster.json'

# ── read current version from the csproj (source of truth) ──────────────────
$text = Get-Content -Raw -Path $csproj
if ($text -notmatch '<Version>(\d+)\.(\d+)\.(\d+)\.(\d+)</Version>') {
    throw "No <Version>X.Y.Z.B</Version> found in $csproj"
}
$maj = [int]$Matches[1]; $min = [int]$Matches[2]; $pat = [int]$Matches[3]; $bld = [int]$Matches[4]

switch ($Part) {
    'major' { $maj++; $min = 0; $pat = 0; $bld = 0 }
    'minor' { $min++; $pat = 0; $bld = 0 }
    'patch' { $pat++; $bld = 0 }
    'build' { $bld++ }
    'sync'  { }   # keep the current version, only re-propagate
}
$new = "$maj.$min.$pat.$bld"

# ── write the csproj ────────────────────────────────────────────────────────
if ($Part -ne 'sync') {
    $text = [regex]::Replace($text, '<Version>\d+\.\d+\.\d+\.\d+</Version>', "<Version>$new</Version>", 1)
    Set-Content -Path $csproj -Value $text -NoNewline -Encoding utf8
}

# ── propagate to pluginmaster.json (AssemblyVersion + release tag in URLs) ───
if (Test-Path $pluginmaster) {
    $pm = Get-Content -Raw -Path $pluginmaster
    $pm = [regex]::Replace($pm, '("AssemblyVersion"\s*:\s*")\d+\.\d+\.\d+\.\d+(")', "`${1}$new`${2}")
    $pm = [regex]::Replace($pm, '(releases/download/)\d+\.\d+\.\d+\.\d+(/)',          "`${1}$new`${2}")
    Set-Content -Path $pluginmaster -Value $pm -NoNewline -Encoding utf8
    Write-Host "pluginmaster.json -> $new"
}

Write-Host "BodyFlux version  -> $new"
