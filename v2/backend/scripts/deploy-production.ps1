<#
.SYNOPSIS
    Deploys the AXE v2 Edge Functions to the production Supabase project and sets the server secrets.

.DESCRIPTION
    Run from v2\backend in your own PowerShell. Reads secrets from a folder OUTSIDE the repository
    (default C:\Users\iaman\axe-production-secrets) and prompts for the invitation codes; nothing secret is
    written into the repo or printed. Optionally sets the FCM service-account secret from a JSON file.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\deploy-production.ps1
    powershell -ExecutionPolicy Bypass -File scripts\deploy-production.ps1 -FcmServiceAccountFile C:\secure\firebase-sa.json
#>
[CmdletBinding()]
param(
    [string]$ProjectRef = 'hjdezbupinqnfyjspwki',
    [string]$SecretsDir = 'C:\Users\iaman\axe-production-secrets',
    [string]$FcmServiceAccountFile,
    # Only set the FCM secret (leaves the signing key, hash secret, invitation codes and functions untouched).
    [switch]$FcmOnly
)

$ErrorActionPreference = 'Stop'
Set-Location (Split-Path -Parent $PSScriptRoot)

$env:SUPABASE_ACCESS_TOKEN = (Get-Content (Join-Path $SecretsDir 'supabase-token.txt') -Raw).Trim()

if ($FcmOnly) {
    if (-not $FcmServiceAccountFile) { throw '-FcmOnly needs -FcmServiceAccountFile.' }
    $sa = Get-Content $FcmServiceAccountFile -Raw | ConvertFrom-Json
    if ($sa.type -ne 'service_account' -or -not $sa.private_key) { throw 'Not a service-account key file.' }
    $envFile = Join-Path $SecretsDir 'secrets.tmp.env'
    try {
        Set-Content -Path $envFile -Value "FCM_SERVICE_ACCOUNT_JSON=$((Get-Content $FcmServiceAccountFile -Raw) -replace '\s*\r?\n\s*', ' ')" -Encoding ascii
        npx --yes supabase secrets set --env-file $envFile --project-ref $ProjectRef
        if ($LASTEXITCODE -ne 0) { throw 'Setting the FCM secret failed.' }
    }
    finally {
        if (Test-Path $envFile) { Remove-Item $envFile -Force }
    }
    Write-Host 'FCM_SERVICE_ACCOUNT_JSON set.'
    return
}

$jwk = (Get-Content (Join-Path $SecretsDir 'signing-private.jwk.json') -Raw).Trim()
$hash = (Get-Content (Join-Path $SecretsDir 'hash-secret.txt') -Raw).Trim()
if (-not $jwk -or -not $hash) { throw 'Signing key or hash secret file is empty.' }

$codes = Read-Host 'Invitation codes, comma separated (use NEW codes; any code that was ever committed or shared is public)'
if (-not $codes.Trim()) { throw 'At least one invitation code is required.' }

# Secrets go through a temporary env file in the secrets folder (outside the repo), removed afterwards.
$envFile = Join-Path $SecretsDir 'secrets.tmp.env'
try {
    $lines = @(
        "AXE_SIGNING_PRIVATE_JWK=$jwk",
        "AXE_HASH_SECRET=$hash",
        "AXE_INVITE_CODES=$($codes.Trim())"
    )
    if ($FcmServiceAccountFile) {
        $lines += "FCM_SERVICE_ACCOUNT_JSON=$((Get-Content $FcmServiceAccountFile -Raw) -replace '\s*\r?\n\s*', ' ')"
    }
    Set-Content -Path $envFile -Value $lines -Encoding ascii
    npx --yes supabase secrets set --env-file $envFile --project-ref $ProjectRef
    if ($LASTEXITCODE -ne 0) { throw 'Setting secrets failed.' }
}
finally {
    if (Test-Path $envFile) { Remove-Item $envFile -Force }
}

npx --yes supabase functions deploy access admin --project-ref $ProjectRef --no-verify-jwt
if ($LASTEXITCODE -ne 0) { throw 'Function deployment failed.' }

Write-Host "`nDeployed. Secret NAMES now set (values are never shown):"
npx --yes supabase secrets list --project-ref $ProjectRef
