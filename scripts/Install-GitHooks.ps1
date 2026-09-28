#!/usr/bin/env pwsh
<#
.SYNOPSIS
    One-time setup: activates the repo's shared git hooks (secret-scanning pre-commit, etc).
.DESCRIPTION
    Points core.hooksPath at the checked-in githooks/ folder and ensures the
    hook scripts are executable (needed on macOS/Linux; harmless on Windows).
#>

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$hooksDir = Join-Path $repositoryRoot 'githooks'

if (-not (Test-Path $hooksDir)) {
    throw "githooks folder not found at $hooksDir"
}

& git -C $repositoryRoot config core.hooksPath githooks
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

Get-ChildItem -Path $hooksDir -File | ForEach-Object {
    if ($IsLinux -or $IsMacOS) {
        & chmod +x $_.FullName
    }
}

Write-Host "Git hooks installed: core.hooksPath -> githooks/"
