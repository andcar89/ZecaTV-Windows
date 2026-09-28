<#
.SYNOPSIS
    Publishes ZecaTV as a self-contained build (no .NET install required on the target machine) and
    zips it into a portable copy. This is also the exact output ZecaTV.iss packages into the
    installer, so this script is the one thing to run before building either.

.EXAMPLE
    .\publish.ps1
    Builds .\publish\win-x64\ and .\publish\ZecaTV-Portable-win-x64.zip

.EXAMPLE
    .\publish.ps1 -Runtime win-x86
    Same, for 32-bit Windows.
#>
param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64"
)
$ErrorActionPreference = "Stop"

$installerDir = $PSScriptRoot
$repoRoot = Split-Path -Parent $installerDir
$csproj = Join-Path $repoRoot "ZecaTV\ZecaTV.csproj"
$publishDir = Join-Path $repoRoot "publish\$Runtime"
$portableZip = Join-Path $repoRoot "publish\ZecaTV-Portable-$Runtime.zip"

Write-Host "Publishing ZecaTV ($Configuration, $Runtime, self-contained)..."
if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }

dotnet publish $csproj -c $Configuration -r $Runtime --self-contained true -o $publishDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit code $LASTEXITCODE)" }

Write-Host "Zipping portable build..."
if (Test-Path $portableZip) { Remove-Item $portableZip -Force }
Compress-Archive -Path (Join-Path $publishDir "*") -DestinationPath $portableZip

Write-Host ""
Write-Host "Done:"
Write-Host "  Publish output (also the installer's source): $publishDir"
Write-Host "  Portable zip - copy this anywhere and run ZecaTV.exe, no installer or .NET needed: $portableZip"
Write-Host ""
Write-Host "To also build the setup.exe installer, run build-installer.ps1 (needs Inno Setup)."
