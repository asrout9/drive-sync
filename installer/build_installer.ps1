# DriveSync Automated Installer Build Script
# 1. Publishes Release self-contained win-x64 binaries
# 2. Compiles Inno Setup installer executable

$ErrorActionPreference = "Stop"
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Definition
$rootDir = (Get-Item "$scriptDir\..").FullName

Write-Host "==========================================" -ForegroundColor Cyan
Write-Host " Building DriveSync Windows Installer" -ForegroundColor Cyan
Write-Host "==========================================" -ForegroundColor Cyan

# Step 1: Publish
Write-Host "[1/2] Publishing Release win-x64 self-contained package..." -ForegroundColor Yellow
dotnet publish "$rootDir\src\DriveSync.App\DriveSync.App.csproj" -c Release -r win-x64 --self-contained true -o "$rootDir\dist\app"

# Step 2: Locate Inno Setup Compiler
$isccPath = & {
    $cmd = Get-Command "ISCC.exe" -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    $local = "C:\Users\$env:USERNAME\AppData\Local\Programs\Inno Setup 6\ISCC.exe"
    if (Test-Path $local) { return $local }
    $prog64 = "C:\Program Files (x86)\Inno Setup 6\ISCC.exe"
    if (Test-Path $prog64) { return $prog64 }
    $prog = "C:\Program Files\Inno Setup 6\ISCC.exe"
    if (Test-Path $prog) { return $prog }
    throw "Inno Setup Compiler (ISCC.exe) not found. Please install Inno Setup 6."
}

Write-Host "[2/2] Compiling Windows Installer with Inno Setup..." -ForegroundColor Yellow
& $isccPath "$scriptDir\DriveSync.iss"

$installerPath = "$rootDir\dist\installer\DriveSync-Setup-v1.0.exe"
if (Test-Path $installerPath) {
    $sizeMb = (Get-Item $installerPath).Length / 1MB
    Write-Host ""
    Write-Host "==========================================" -ForegroundColor Green
    Write-Host " BUILD SUCCESSFUL!" -ForegroundColor Green
    Write-Host " Output: $installerPath ($([math]::Round($sizeMb, 1)) MB)" -ForegroundColor Green
    Write-Host "==========================================" -ForegroundColor Green
} else {
    throw "Installer executable was not generated."
}
