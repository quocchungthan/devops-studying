namespace Farm.Core.Chickens;

public static class RestrictedProcessEnvironment
{
    private static readonly string[] AllowedNames =
    [
        "PATH", "PATHEXT", "SystemRoot", "WINDIR", "COMSPEC", "TEMP", "TMP", "TMPDIR",
        "HOME", "USERPROFILE", "LOCALAPPDATA", "APPDATA", "TZ", "LANG", "LC_ALL",
        "DOTNET_ROOT", "DOTNET_ROOT_X64", "DOTNET_CLI_HOME", "DOTNET_NOLOGO",
        "DOTNET_SKIP_FIRST_TIME_EXPERIENCE", "DOTNET_CLI_TELEMETRY_OPTOUT",
        "DOTNET_MULTILEVEL_LOOKUP", "DOTNET_SYSTEM_GLOBALIZATION_INVARIANT", "NUGET_PACKAGES"
    ];

    public static Dictionary<string, string> Create(IReadOnlyDictionary<string, string>? explicitlySafe = null)
    {
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in AllowedNames)
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrEmpty(value))
            {
                environment[name] = value;
            }
        }

        if (explicitlySafe is not null)
        {
            foreach (var variable in explicitlySafe)
            {
                if (!IsForbidden(variable.Key, variable.Value))
                {
                    environment[variable.Key] = variable.Value;
                }
            }
        }

        environment["GIT_TERMINAL_PROMPT"] = "0";
        return environment;
    }

    private static bool IsForbidden(string name, string value)
    {
        var normalized = name.ToUpperInvariant();
        if (normalized == "FARM_AZURE_DEVOPS_PAT" || normalized.StartsWith("COPILOT", StringComparison.Ordinal) ||
            normalized is "GITHUB_TOKEN" or "GH_TOKEN" or "GIT_ASKPASS" or "SSH_ASKPASS" ||
            normalized.StartsWith("GIT_AUTH", StringComparison.Ordinal) ||
            normalized.StartsWith("GIT_CONFIG", StringComparison.Ordinal) ||
            normalized.Contains("CREDENTIAL", StringComparison.Ordinal) ||
            normalized.EndsWith("_TOKEN", StringComparison.Ordinal) || normalized.EndsWith("_PAT", StringComparison.Ordinal) ||
            normalized.EndsWith("_SECRET", StringComparison.Ordinal) || normalized.EndsWith("_PASSWORD", StringComparison.Ordinal))
        {
            return true;
        }

        return normalized is "HTTP_PROXY" or "HTTPS_PROXY" or "ALL_PROXY" &&
            Uri.TryCreate(value, UriKind.Absolute, out var proxy) && !string.IsNullOrEmpty(proxy.UserInfo);
    }
}