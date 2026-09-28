#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Exports the Cisco Umbrella corporate root CA cert from the local Windows trust store
    into deployment/cisco-umbrella-root-ca.crt.
.DESCRIPTION
    Farm.Sandbox.Chickens/Dockerfile COPYs deployment/cisco-umbrella-root-ca.crt and runs
    update-ca-certificates so outbound HTTPS calls from inside the sandbox image succeed
    behind the corporate TLS-inspecting proxy. The file is environment-specific (gitignored)
    and must be regenerated locally before building that image.

    Re-run this script when:
      - Setting up a fresh dev machine before building Farm.Sandbox.Chickens.
      - The corporate proxy rotates its root CA (image builds start failing with UnknownIssuer).
      - deployment/cisco-umbrella-root-ca.crt is missing or was deleted.
#>

[CmdletBinding()]
param(
    [string]$CertificateSubjectMatch = 'Cisco Umbrella',
    [string]$OutputPath = (Join-Path (Split-Path -Parent $PSScriptRoot) 'deployment/cisco-umbrella-root-ca.crt')
)

$ErrorActionPreference = 'Stop'

$cert = Get-ChildItem -Path Cert:\LocalMachine\Root, Cert:\CurrentUser\Root |
    Where-Object { $_.Subject -like "*$CertificateSubjectMatch*" } |
    Select-Object -First 1

if (-not $cert) {
    throw "No root CA certificate matching '$CertificateSubjectMatch' found in the Windows trust store."
}

$outputDirectory = Split-Path -Parent $OutputPath
New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null

Export-Certificate -Cert $cert -FilePath $OutputPath -Type CERT | Out-Null

# Convert from DER to PEM so update-ca-certificates (inside the Linux build image) can read it.
$derBytes = [System.IO.File]::ReadAllBytes($OutputPath)
$base64 = [System.Convert]::ToBase64String($derBytes, [System.Base64FormattingOptions]::InsertLineBreaks)
$pem = "-----BEGIN CERTIFICATE-----`n$base64`n-----END CERTIFICATE-----`n"
Set-Content -Path $OutputPath -Value $pem -NoNewline -Encoding ascii

Write-Host "Exported '$($cert.Subject)' to $OutputPath"
