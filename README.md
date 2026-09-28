# devops-study

.NET solution for the Azure DevOps review tooling extracted from `internet-facing`:

- [Farm.Sandbox.Chickens](Farm.Sandbox.Chickens/README.md) is the scheduled Azure DevOps review-feedback worker and its container deployment.
- [Farm.Console](Farm.Console/README.md) is the Azure DevOps CLI and .NET tool, installed as `sam`.
- `Farm.Core`, `Farm.Azure`, `Farm.Git`, `Farm.Copilot` and `Farm.State.Sqlite` are the shared libraries they build on.

## Build and test

```powershell
dotnet build DevopsStudy.slnx -warnaserror
dotnet test DevopsStudy.slnx --no-build
scripts/Invoke-FarmValidation.ps1 -Scope Chickens   # or Core / Solution; add -AuditPackages for the NuGet audit
scripts/Build-FarmSandboxChickensImage.ps1          # publish + docker build of the Chickens image
```

The Chickens image needs `deployment/cisco-umbrella-root-ca.crt` behind the corporate proxy; regenerate it with `scripts/Export-CorporateRootCA.ps1`.

## Git hooks

Run `scripts/Install-GitHooks.ps1` once after cloning to set `core.hooksPath` to `githooks/`. The `pre-commit` hook blocks staged secrets, `.env` files and `deployment/*.crt|*.pem`.
