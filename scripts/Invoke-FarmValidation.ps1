param(
    [ValidateSet('Core', 'Chickens', 'Solution')]
    [string]$Scope = 'Solution',
    [switch]$AuditPackages
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot

$targets = switch ($Scope) {
    'Core' { @('Farm.Core.Tests/Farm.Core.Tests.csproj') }
    'Chickens' {
        @(
            'Farm.Core.Tests/Farm.Core.Tests.csproj',
            'Farm.Git.Tests/Farm.Git.Tests.csproj',
            'Farm.State.Sqlite.Tests/Farm.State.Sqlite.Tests.csproj',
            'Farm.Sandbox.Chickens.Tests/Farm.Sandbox.Chickens.Tests.csproj'
        )
    }
    default { @('DevopsStudy.slnx') }
}

foreach ($relativeTarget in $targets) {
    & dotnet test (Join-Path $repositoryRoot $relativeTarget) --no-restore
    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }
}

if ($AuditPackages) {
    foreach ($relativeTarget in $targets) {
        & dotnet list (Join-Path $repositoryRoot $relativeTarget) package --vulnerable --include-transitive --no-restore
        if ($LASTEXITCODE -ne 0) {
            exit $LASTEXITCODE
        }
    }
}

exit 0