<#
.SYNOPSIS
    Builds, tests and packages AXE v2 into ONE installer: v2\release-v2\AXE-v2-Setup.exe

.DESCRIPTION
    1. Runs the unit tests (development config allowed ONLY for the tests).
    2. Publishes AXE v2 self-contained for win-x64 with the PRODUCTION server configuration
       (-ServerConfig; HTTPS only, anon key + signing PUBLIC key only - never a secret).
    3. Verifies the publish output holds no development configuration and no known secret material.
    4. Fetches/validates Microsoft's WebView2 Evergreen Bootstrapper.
    5. Compiles installer\AXE-v2.iss with Inno Setup 6 and records the SHA-256.

    The Release build itself refuses a development/loopback server.json, so a test build can never be shipped by mistake.
    Nothing here signs the installer: it is reported as UNSIGNED unless you sign it yourself afterwards.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File build\build-release-v2.ps1 -ServerConfig C:\secure\axe-v2-production-server.json
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ServerConfig,
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'
$v2 = Split-Path -Parent $PSScriptRoot
$publishDir = Join-Path $v2 'artifacts-v2\publish'
$redistDir = Join-Path $v2 'installer\redist'
$releaseDir = Join-Path $v2 'release-v2'
$bootstrapper = Join-Path $redistDir 'MicrosoftEdgeWebview2Setup.exe'

function Step($text) { Write-Host "`n==> $text" -ForegroundColor Cyan }

$ServerConfig = (Resolve-Path $ServerConfig).Path
$cfg = Get-Content $ServerConfig -Raw | ConvertFrom-Json
foreach ($name in 'functionsUrl', 'anonKey', 'signingPublicKey') {
    if (-not $cfg.$name) { throw "Server config is missing '$name'." }
}
if ($cfg.functionsUrl -notmatch '^https://' -or $cfg.functionsUrl -match '//(localhost|127\.|10\.0\.2\.2|\[::1\])') {
    throw 'Server config must point to a real HTTPS production endpoint (no http, no localhost).'
}
$extra = @($cfg.PSObject.Properties.Name | Where-Object { $_ -notin 'functionsUrl', 'anonKey', 'signingPublicKey' })
if ($extra.Count -gt 0) { throw "Server config has unexpected fields: $($extra -join ', ')" }
if ($cfg.anonKey -match 'service_role|sb_secret') { throw 'The server config contains a service key. Only the public anon key may be shipped.' }

if (-not $SkipTests) {
    Step 'Running unit tests'
    dotnet test (Join-Path $v2 'tests\AXEv2.Tests\AXEv2.Tests.csproj') -c Release -p:AxeAllowLocalConfig=true
    if ($LASTEXITCODE -ne 0) { throw 'Unit tests failed.' }
}

Step 'Publishing AXE v2 (self-contained, win-x64, production config)'
if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
dotnet publish (Join-Path $v2 'src\AXEv2\AXEv2.csproj') -c Release -r win-x64 --self-contained true `
    "-p:AxeServerConfig=$ServerConfig" -p:PublishReadyToRun=true -p:DebugType=none -p:DebugSymbols=false -o $publishDir
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }

Step 'Checking the publish output for development configuration and secrets'
$forbidden = @('service_role', 'PRIVATE KEY', 'AXE_SIGNING_PRIVATE_JWK', 'AXE_HASH_SECRET', 'FCM_SERVICE_ACCOUNT')
foreach ($name in 'AXE-v2.exe', 'AXE-v2.dll') {
    $bytes = [System.IO.File]::ReadAllBytes((Join-Path $publishDir $name))
    $asciiText = [System.Text.Encoding]::ASCII.GetString($bytes)
    $unicodeText = [System.Text.Encoding]::Unicode.GetString($bytes)
    foreach ($needle in $forbidden) {
        if ($asciiText.Contains($needle) -or $unicodeText.Contains($needle)) { throw "Forbidden text '$needle' found in $name." }
    }
}
Get-ChildItem $publishDir -Recurse -File | Where-Object { $_.Name -match '\.(env|pem|jwk|keystore|jks)$|server\.json$|local-admin' } |
    ForEach-Object { throw "Unexpected configuration/secret file in the publish output: $($_.FullName)" }

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
& $iscc (Join-Path $v2 'installer\AXE-v2.iss')
if ($LASTEXITCODE -ne 0) { throw 'Inno Setup compilation failed.' }

$files = @(Get-ChildItem $releaseDir -File)
if ($files.Count -ne 1 -or $files[0].Name -ne 'AXE-v2-Setup.exe') {
    throw "release-v2\ must contain exactly AXE-v2-Setup.exe, found: $($files.Name -join ', ')"
}

$setup = $files[0]
$hash = (Get-FileHash $setup.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
$sig = (Get-AuthenticodeSignature $setup.FullName).Status
Step ("Done: {0} ({1:N1} MB)" -f $setup.FullName, ($setup.Length / 1MB))
Write-Host "SHA-256  : $hash"
Write-Host "Signature: $sig  (NotSigned means the installer is NOT code-signed; Windows SmartScreen will warn)"
Write-Host "Endpoint : $($cfg.functionsUrl)"
