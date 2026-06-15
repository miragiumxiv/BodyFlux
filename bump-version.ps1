#!/usr/bin/env pwsh
# Bumps <Version> (Major.Minor.Patch.Build) in BodyFlux/BodyFlux.csproj.
#   ./bump-version.ps1            # build:  0.5.0.0 -> 0.5.0.1   (also what the pre-commit hook does)
#   ./bump-version.ps1 patch      # patch:  0.5.0.4 -> 0.5.1.0
#   ./bump-version.ps1 minor      # minor:  0.5.3.2 -> 0.6.0.0   (e.g. 0.5 -> 0.6)
#   ./bump-version.ps1 major      # major:  0.6.4.1 -> 1.0.0.0
param([ValidateSet('build', 'patch', 'minor', 'major')][string]$Part = 'build')

$ErrorActionPreference = 'Stop'
$csproj = Join-Path $PSScriptRoot 'BodyFlux/BodyFlux.csproj'
$text   = Get-Content -Raw -Path $csproj

if ($text -notmatch '<Version>(\d+)\.(\d+)\.(\d+)\.(\d+)</Version>') {
    throw "No <Version>X.Y.Z.B</Version> found in $csproj"
}
$maj = [int]$Matches[1]; $min = [int]$Matches[2]; $pat = [int]$Matches[3]; $bld = [int]$Matches[4]

switch ($Part) {
    'major' { $maj++; $min = 0; $pat = 0; $bld = 0 }
    'minor' { $min++; $pat = 0; $bld = 0 }
    'patch' { $pat++; $bld = 0 }
    default { $bld++ }
}
$new = "$maj.$min.$pat.$bld"

$rx   = [regex]'<Version>\d+\.\d+\.\d+\.\d+</Version>'
$text = $rx.Replace($text, "<Version>$new</Version>", 1)
Set-Content -Path $csproj -Value $text -NoNewline -Encoding utf8

Write-Host "BodyFlux version -> $new"
