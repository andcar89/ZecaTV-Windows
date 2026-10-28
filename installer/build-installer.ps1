<#
.SYNOPSIS
    Builds ZecaTV's setup.exe installer with Inno Setup. Publishes the app first (see publish.ps1),
    then compiles ZecaTV.iss.

.NOTES
    Needs Inno Setup installed once — it's free: https://jrsoftware.org/isdl.php
    (installs ISCC.exe, its command-line compiler, alongside the Inno Setup app you can also open
    ZecaTV.iss in directly and click Compile, if you'd rather not use this script at all.)

.EXAMPLE
    .\build-installer.ps1
#>
param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64"
)
$ErrorActionPreference = "Stop"
$installerDir = $PSScriptRoot

& (Join-Path $installerDir "publish.ps1") -Configuration $Configuration -Runtime $Runtime

# Find ISCC.exe: on PATH first, then in Inno Setup's usual install folders, whichever version
# is installed (5, 6, 7...), per-machine (Program Files) or per-user (AppData\Local\Programs).
$isccPath = $null
$onPath = Get-Command "ISCC.exe" -ErrorAction SilentlyContinue
if ($onPath) { $isccPath = $onPath.Source }
if (-not $isccPath) {
    $roots = @(${env:ProgramFiles(x86)}, $env:ProgramFiles, (Join-Path $env:LOCALAPPDATA "Programs")) |
        Where-Object { $_ -and (Test-Path $_) }
    $isccPath = $roots |
        ForEach-Object { Get-ChildItem -Path $_ -Directory -Filter "Inno Setup*" -ErrorAction SilentlyContinue } |
        ForEach-Object { Join-Path $_.FullName "ISCC.exe" } |
        Where-Object { Test-Path $_ } |
        Sort-Object -Descending |
        Select-Object -First 1
}
if (-not $isccPath) {
    Write-Host ""
    Write-Host "Inno Setup's compiler (ISCC.exe) wasn't found on PATH or in its usual install folders."
    Write-Host "Install Inno Setup (free): https://jrsoftware.org/isdl.php"
    Write-Host "Then either re-run this script, or open installer\ZecaTV.iss in the Inno Setup app and click Compile."
    exit 1
}

Write-Host ""
Write-Host "Compiling installer with $isccPath..."
& $isccPath (Join-Path $installerDir "ZecaTV.iss")
if ($LASTEXITCODE -ne 0) { throw "ISCC failed (exit code $LASTEXITCODE)" }

Write-Host ""
Write-Host "Installer ready: installer\Output\"
