# Build and Package Script for Graveyard Keeper Back From The Grave
# Creates the mod release package

param(
    [string]$Version = "1.0.0"
)

$ErrorActionPreference = "Stop"
$RootDir = Split-Path -Parent $PSScriptRoot
$OutputDir = Join-Path $RootDir "releases"

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Graveyard Keeper Back From The Grave Build Script" -ForegroundColor Cyan
Write-Host "Version: $Version" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan

# Create output directory
if (!(Test-Path $OutputDir)) {
    New-Item -ItemType Directory -Path $OutputDir | Out-Null
}

Write-Host "`n[1/2] Building mod..." -ForegroundColor Yellow
Push-Location $RootDir

dotnet build --configuration Release -p:ModVersion=$Version
if ($LASTEXITCODE -ne 0) {
    Write-Host "Mod build failed!" -ForegroundColor Red
    exit 1
}

Pop-Location
Write-Host "Mod build successful!" -ForegroundColor Green

Write-Host "`n[2/2] Packaging mod..." -ForegroundColor Yellow

$ModPackageDir = Join-Path $OutputDir "mod_package"
$ModZipPath = Join-Path $OutputDir "GraveyardKeeperCoop_v$Version.zip"

if (Test-Path $ModPackageDir) {
    Remove-Item -Recurse -Force $ModPackageDir
}
if (Test-Path $ModZipPath) {
    Remove-Item -Force $ModZipPath
}

$PluginDir = Join-Path $ModPackageDir "GraveyardKeeperCoop"
New-Item -ItemType Directory -Path $PluginDir | Out-Null

$BinDir = Join-Path $RootDir "bin\Release\net46"
$GeneratedModInfo = Join-Path $RootDir "obj\Generated\modinfo.txt"
Copy-Item (Join-Path $BinDir "GraveyardKeeperCoop.dll") $PluginDir
if (Test-Path $GeneratedModInfo) {
    Copy-Item $GeneratedModInfo (Join-Path $PluginDir "modinfo.txt")
}

Compress-Archive -Path "$PluginDir\*" -DestinationPath $ModZipPath -Force
Remove-Item -Recurse -Force $ModPackageDir
Write-Host "Mod packaged: $ModZipPath" -ForegroundColor Green

Write-Host "`n========================================" -ForegroundColor Cyan
Write-Host "Build complete!" -ForegroundColor Green
Write-Host "Output directory: $OutputDir" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan

# List created files
Write-Host "`nCreated files:" -ForegroundColor Yellow
Get-ChildItem $OutputDir -Filter "*.zip" | ForEach-Object {
    $size = [math]::Round($_.Length / 1KB, 2)
    Write-Host "  - $($_.Name) ($size KB)" -ForegroundColor White
}
