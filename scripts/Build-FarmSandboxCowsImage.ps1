[CmdletBinding()]
param(
    [string]$ImageTag = 'farm-sandbox-cows:local',
    [switch]$SkipDockerBuild
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repositoryRoot 'Farm.Sandbox.Cows/Farm.Sandbox.Cows.csproj'
$solutionPath = Join-Path $repositoryRoot 'DevopsStudy.slnx'
$validationScript = Join-Path $PSScriptRoot 'Invoke-FarmValidation.ps1'
$publishDirectory = Join-Path $repositoryRoot 'artifacts/farm-sandbox-cows/linux-x64'
$publishRoot = Split-Path -Parent $publishDirectory
$stagingDirectory = Join-Path $publishRoot ".linux-x64-$([Guid]::NewGuid().ToString('N'))"
$runtimeIdentifier = 'linux-x64'

function Invoke-CheckedCommand {
    param(
        [Parameter(Mandatory)]
        [string]$FilePath,
        [Parameter(Mandatory)]
        [string[]]$ArgumentList
    )

    & $FilePath @ArgumentList
    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }
}

Push-Location $repositoryRoot
try {
    New-Item -ItemType Directory -Path $publishRoot -Force | Out-Null
    if (Test-Path $publishDirectory) {
        Remove-Item $publishDirectory -Recurse -Force
    }

    Invoke-CheckedCommand 'dotnet' @('clean', $solutionPath, '--configuration', 'Debug')
    Invoke-CheckedCommand 'dotnet' @('clean', $solutionPath, '--configuration', 'Release')
    Invoke-CheckedCommand 'dotnet' @('restore', $solutionPath)
    Invoke-CheckedCommand 'powershell.exe' @(
        '-NoProfile',
        '-ExecutionPolicy', 'Bypass',
        '-File', $validationScript,
        '-Scope', 'Cows'
    )
    Invoke-CheckedCommand 'dotnet' @(
        'restore', $projectPath,
        '--runtime', $runtimeIdentifier,
        "/p:CopilotRuntimeIdentifier=$runtimeIdentifier"
    )

    Invoke-CheckedCommand 'dotnet' @(
        'publish', $projectPath,
        '--configuration', 'Release',
        '--no-restore',
        '--runtime', $runtimeIdentifier,
        '--self-contained', 'false',
        '--output', $stagingDirectory,
        "/p:CopilotRuntimeIdentifier=$runtimeIdentifier",
        '/p:UseAppHost=false'
    )

    if (Test-Path $publishDirectory) {
        Remove-Item $publishDirectory -Recurse -Force
    }
    [System.IO.Directory]::Move($stagingDirectory, $publishDirectory)

    if (-not $SkipDockerBuild) {
        Invoke-CheckedCommand 'docker' @(
            'build',
            '--file', 'Farm.Sandbox.Cows/Dockerfile',
            '--tag', $ImageTag,
            '.'
        )
    }
}
finally {
    if (Test-Path $stagingDirectory) {
        Remove-Item $stagingDirectory -Recurse -Force
    }
    Pop-Location
}
