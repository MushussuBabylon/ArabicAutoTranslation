<#
.SYNOPSIS
    Assembles the ready-to-install Arabic Auto Translation mod folder from build output.

.DESCRIPTION
    Copies the mod content (About, Languages, Arabic glossary, README, licence) and the freshly built
    ArabicAutoTranslation.dll of every RimWorld version found under <BuildRoot>/<version>/ into <Output>,
    together with the bundled 0Harmony.dll of that version (RimWorld 1.7 has none: there Harmony is a mod
    dependency). The 'Source', 'Tools' and '.github' folders are intentionally left out - the result is the
    folder that goes into RimWorld's Mods directory.

    On GitHub Actions a short table of the included versions is added to the run summary.

.EXAMPLE
    ./Tools/package_mod.ps1 -BuildRoot build -Output dist/ArabicAutoTranslation
#>
param(
    [string]$BuildRoot = 'build',
    [string]$Output = 'dist/ArabicAutoTranslation'
)

$ErrorActionPreference = 'Stop'

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if (-not [System.IO.Path]::IsPathRooted($BuildRoot)) { $BuildRoot = Join-Path $repoRoot $BuildRoot }
if (-not [System.IO.Path]::IsPathRooted($Output)) { $Output = Join-Path $repoRoot $Output }

if (Test-Path $Output) { Remove-Item $Output -Recurse -Force }
New-Item -ItemType Directory -Path $Output -Force | Out-Null

# --- mod content --------------------------------------------------------------------------------
foreach ($name in 'About', 'Languages', 'Arabic') {
    $src = Join-Path $repoRoot $name
    if (Test-Path $src) {
        Copy-Item -Path $src -Destination (Join-Path $Output $name) -Recurse -Force
    }
}
foreach ($name in 'LICENSE', 'README.md', 'THIRD_PARTY_NOTICES.md') {
    $src = Join-Path $repoRoot $name
    if (Test-Path $src) {
        Copy-Item -Path $src -Destination $Output -Force
    }
}

# --- assemblies ---------------------------------------------------------------------------------
$included = New-Object System.Collections.Generic.List[string]
$summary = New-Object System.Collections.Generic.List[string]
$summary.Add('### Arabic Auto Translation mod folder')
$summary.Add('')
$summary.Add('| RimWorld | Assemblies/ArabicAutoTranslation.dll |')
$summary.Add('|---|---|')

foreach ($version in '1.4', '1.5', '1.6', '1.7') {
    $dll = Join-Path (Join-Path $BuildRoot $version) 'ArabicAutoTranslation.dll'
    if (-not (Test-Path $dll)) {
        Write-Host "[package] RimWorld $version : no built ArabicAutoTranslation.dll found - folder not included."
        $summary.Add("| $version | not included (not built) |")
        continue
    }

    $dest = Join-Path $Output "$version/Assemblies"
    New-Item -ItemType Directory -Path $dest -Force | Out-Null
    Copy-Item -Path $dll -Destination $dest -Force

    $harmony = Join-Path $repoRoot "$version/Assemblies/0Harmony.dll"
    if (Test-Path $harmony) { Copy-Item -Path $harmony -Destination $dest -Force }

    $hash = (Get-FileHash -Path (Join-Path $dest 'ArabicAutoTranslation.dll') -Algorithm SHA256).Hash
    $included.Add("RimWorld $version  ArabicAutoTranslation.dll  sha256=$hash")
    $summary.Add("| $version | included |")
    Write-Host "[package] RimWorld $version : included."
}

if ($included.Count -eq 0) {
    throw 'No ArabicAutoTranslation.dll was found - nothing to package. Run the build first.'
}

$info = @(
    'Arabic Auto Translation: ready-to-install mod folder',
    ('Assembled (UTC): ' + [DateTime]::UtcNow.ToString('yyyy-MM-dd HH:mm:ss')),
    ''
) + $included.ToArray()
Set-Content -Path (Join-Path $Output 'BUILD-INFO.txt') -Value $info -Encoding UTF8

if ($env:GITHUB_STEP_SUMMARY) {
    Add-Content -Path $env:GITHUB_STEP_SUMMARY -Value $summary.ToArray() -Encoding UTF8
}

Write-Host "[package] Done: $Output"
