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

$iscc = Get-Command "ISCC.exe" -ErrorAction SilentlyContinue
if (-not $iscc) {
    $defaultPath = "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe"
    if (Test-Path $defaultPath) { $iscc = Get-Item $defaultPath }
}
if (-not $iscc) {
    Write-Host ""
    Write-Host "Inno Setup's compiler (ISCC.exe) wasn't found on PATH or in its default install location."
    Write-Host "Install Inno Setup (free): https://jrsoftware.org/isdl.php"
    Write-Host "Then either re-run this script, or open installer\ZecaTV.iss in the Inno Setup app and click Compile."
    exit 1
}

Write-Host ""
Write-Host "Compiling installer with $($iscc.Source)..."
& $iscc.Source (Join-Path $installerDir "ZecaTV.iss")
if ($LASTEXITCODE -ne 0) { throw "ISCC failed (exit code $LASTEXITCODE)" }

Write-Host ""
Write-Host "Installer ready: installer\Output\"
