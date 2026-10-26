param(
    [switch]$SkipAppPublish
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$env:PATH = "$env:LOCALAPPDATA\Microsoft\dotnet;$env:PATH"

$publishDir = Join-Path $repoRoot "publish\single"
$installerDir = $PSScriptRoot
$outputDir = Join-Path $installerDir "Output"
$payloadZip = Join-Path $installerDir "payload.zip"

$acroCatSetupOut = Join-Path $outputDir "AcroCatPdfEditor_Setup.exe"
$arcoCatSetupOut = Join-Path $outputDir "ArcoCatPdfEditor_Setup.exe"
$legacySetupOut = Join-Path $outputDir "AcrobatPdfEditor_Setup.exe"
$rootAcroCatSetup = Join-Path $repoRoot "AcroCatPdfEditor_Setup.exe"
$rootArcoCatSetup = Join-Path $repoRoot "ArcoCatPdfEditor_Setup.exe"
$rootLegacySetup = Join-Path $repoRoot "AcrobatPdfEditor_Setup.exe"

$assetsDir = Join-Path $repoRoot "AcrobatPdfEditor\Assets"
$iconPng = Join-Path $assetsDir "ArcoCatIcon.png"
$iconIco = Join-Path $assetsDir "ArcoCatIcon.ico"

Write-Host "==> 0. Ensuring ArcoCat Icon assets exist..."
if (-not (Test-Path $iconIco) -or -not (Test-Path $iconPng)) {
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $installerDir "generate-icon.ps1")
}
Copy-Item $iconPng (Join-Path $repoRoot "ArcoCatIcon.png") -Force
Copy-Item $iconIco (Join-Path $repoRoot "ArcoCatIcon.ico") -Force

Write-Host "==> 1. Stopping any running AcroCatPdfEditor / AcrobatPdfEditor instances..."
Get-Process AcroCatPdfEditor, ArcoCatPdfEditor, AcrobatPdfEditor -ErrorAction SilentlyContinue | Stop-Process -Force

if (-not $SkipAppPublish) {
    Write-Host "==> 2. Publishing Self-Contained Windows x64 AcroCatPdfEditor.exe..."
    if (Test-Path $publishDir) {
        Remove-Item $publishDir -Recurse -Force
    }

    & dotnet publish (Join-Path $repoRoot "AcrobatPdfEditor\AcrobatPdfEditor.csproj") `
        -c Release `
        -r win-x64 `
        --self-contained true `
        -p:PublishSingleFile=true `
        -p:EnableCompressionInSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:DebugType=None `
        -o $publishDir

    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed with exit code $LASTEXITCODE"
    }
}

# Ensure publish directory exists and contains AcroCatPdfEditor.exe (never AcrobatPdfEditor.exe)
New-Item -ItemType Directory -Path $publishDir -Force | Out-Null
$oldPublishedExe = Join-Path $publishDir "AcrobatPdfEditor.exe"
$newPublishedExe = Join-Path $publishDir "AcroCatPdfEditor.exe"
if ((Test-Path $oldPublishedExe) -and -not (Test-Path $newPublishedExe)) {
    Move-Item $oldPublishedExe $newPublishedExe -Force
} elseif (Test-Path $oldPublishedExe) {
    Remove-Item $oldPublishedExe -Force
}

# Copy README, Icon, and Sample PDF into publish staging folder if present
if (Test-Path (Join-Path $repoRoot "README.md")) {
    Copy-Item (Join-Path $repoRoot "README.md") (Join-Path $publishDir "README.md") -Force
}
if (Test-Path $iconIco) {
    Copy-Item $iconIco (Join-Path $publishDir "ArcoCatIcon.ico") -Force
}
if (Test-Path $iconPng) {
    Copy-Item $iconPng (Join-Path $publishDir "ArcoCatIcon.png") -Force
}
if (Test-Path (Join-Path $repoRoot "Sample_Enterprise_Agreement.pdf")) {
    Copy-Item (Join-Path $repoRoot "Sample_Enterprise_Agreement.pdf") (Join-Path $publishDir "Sample_Enterprise_Agreement.pdf") -Force
}

Write-Host "==> 3. Creating compressed installer payload archive..."
if (Test-Path $payloadZip) {
    Remove-Item $payloadZip -Force
}
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory($publishDir, $payloadZip, [System.IO.Compression.CompressionLevel]::Optimal, $false)

Write-Host "==> 4. Compiling Windows GUI Setup Installer with ArcoCat Icon..."
New-Item -ItemType Directory -Path $outputDir -Force | Out-Null

$cscPath = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path $cscPath)) {
    throw "Could not find C# compiler at $cscPath"
}

& $cscPath `
    /nologo `
    /target:winexe `
    /optimize+ `
    "/win32icon:$iconIco" `
    /out:$arcoCatSetupOut `
    /r:System.dll `
    /r:System.Core.dll `
    /r:System.Drawing.dll `
    /r:System.Windows.Forms.dll `
    /r:System.IO.Compression.dll `
    /r:System.IO.Compression.FileSystem.dll `
    /r:Microsoft.CSharp.dll `
    "/res:$payloadZip,payload.zip" `
    "/res:$iconPng,ArcoCatIcon.png" `
    (Join-Path $installerDir "InstallerWizard.cs")

if ($LASTEXITCODE -ne 0) {
    throw "csc.exe failed with exit code $LASTEXITCODE"
}

Copy-Item $arcoCatSetupOut $acroCatSetupOut -Force
Copy-Item $arcoCatSetupOut $legacySetupOut -Force
Copy-Item $arcoCatSetupOut $rootAcroCatSetup -Force
Copy-Item $arcoCatSetupOut $rootArcoCatSetup -Force
Copy-Item $arcoCatSetupOut $rootLegacySetup -Force
Remove-Item $payloadZip -Force -ErrorAction SilentlyContinue

$sizeMB = [math]::Round((Get-Item $arcoCatSetupOut).Length / 1MB, 1)
Write-Host "==> SUCCESS! Windows Installer built with ArcoCat Icon:"
Write-Host "    1) $rootAcroCatSetup ($sizeMB MB)"
Write-Host "    2) $rootArcoCatSetup ($sizeMB MB)"
Write-Host "    3) $acroCatSetupOut ($sizeMB MB)"
