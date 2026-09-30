<#
.SYNOPSIS
    Builds, tests and packages AXE into a single installer: release\AXE-Setup.exe

.DESCRIPTION
    1. Runs the unit tests.
    2. Publishes AXE self-contained for win-x64 (the .NET runtime is bundled, so the
       target PC needs no .NET install).
    3. Downloads Microsoft's WebView2 Evergreen Bootstrapper (if not already cached).
    4. Compiles installer\AXE.iss with Inno Setup 6.
    5. Leaves exactly one distributable file in release\.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File build\build-release.ps1
#>
[CmdletBinding()]
param(
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$publishDir = Join-Path $root 'artifacts\publish'
$redistDir = Join-Path $root 'installer\redist'
$releaseDir = Join-Path $root 'release'
$bootstrapper = Join-Path $redistDir 'MicrosoftEdgeWebview2Setup.exe'

function Step($text) { Write-Host "`n==> $text" -ForegroundColor Cyan }

if (-not $SkipTests) {
    Step 'Running unit tests'
    dotnet test (Join-Path $root 'tests\AXE.Tests\AXE.Tests.csproj') -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Unit tests failed.' }
}

Step 'Publishing AXE (self-contained, win-x64)'
if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
dotnet publish (Join-Path $root 'src\AXE\AXE.csproj') -c Release -r win-x64 --self-contained true `
    -p:PublishReadyToRun=true -p:DebugType=none -p:DebugSymbols=false -o $publishDir
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }

Step 'Fetching WebView2 Evergreen Bootstrapper'
New-Item -ItemType Directory -Force $redistDir | Out-Null
if (-not (Test-Path $bootstrapper)) {
    Invoke-WebRequest -Uri 'https://go.microsoft.com/fwlink/p/?LinkId=2124703' -OutFile $bootstrapper -UseBasicParsing
}
$signature = Get-AuthenticodeSignature $bootstrapper
if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'Microsoft Corporation') {
    throw "WebView2 bootstrapper signature check failed: $($signature.Status)"
}

Step 'Compiling installer'
$iscc = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup 6 (ISCC.exe) not found. Install it from https://jrsoftware.org/isdl.php' }

if (Test-Path $releaseDir) { Remove-Item $releaseDir -Recurse -Force }
& $iscc (Join-Path $root 'installer\AXE.iss')
if ($LASTEXITCODE -ne 0) { throw 'Inno Setup compilation failed.' }

$files = @(Get-ChildItem $releaseDir -File)
if ($files.Count -ne 1 -or $files[0].Name -ne 'AXE-Setup.exe') {
    throw "release\ must contain exactly AXE-Setup.exe, found: $($files.Name -join ', ')"
}

$setup = $files[0]
Step ("Done: {0} ({1:N1} MB)" -f $setup.FullName, ($setup.Length / 1MB))
