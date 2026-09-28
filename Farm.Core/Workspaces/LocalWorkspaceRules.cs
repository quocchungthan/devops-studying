namespace Farm.Core.Workspaces;

public static class LocalWorkspaceRules
{
    private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL"
    };

    public static string GetWorkspaceDirectory(string outputRootPath, string workspaceName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputRootPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceName);

        if (!Path.IsPathFullyQualified(outputRootPath))
        {
            throw new ArgumentException("The workspace output root must be an absolute path.", nameof(outputRootPath));
        }

        if (!IsSafeWorkspaceName(workspaceName))
        {
            throw new ArgumentException("The workspace name may contain only letters, digits, periods, underscores, and hyphens.", nameof(workspaceName));
        }

        var root = Path.GetFullPath(outputRootPath);
        var workspacePath = Path.GetFullPath(Path.Combine(root, workspaceName));
        var relativePath = Path.GetRelativePath(root, workspacePath);

        if (relativePath == ".." || relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
        {
            throw new ArgumentException("The workspace path must remain within the output root.", nameof(workspaceName));
        }

        return workspacePath;
    }

    public static string EnsureWorkspaceDirectory(string outputRootPath, string workspaceName)
    {
        var workspaceDirectory = GetWorkspaceDirectory(outputRootPath, workspaceName);
        Directory.CreateDirectory(workspaceDirectory);
        return workspaceDirectory;
    }

    public static string GetManifestPath(string outputRootPath, string workspaceName) =>
        Path.Combine(GetWorkspaceDirectory(outputRootPath, workspaceName), "manifest.json");

    private static bool IsSafeWorkspaceName(string workspaceName)
    {
        if (workspaceName is "." or ".."
            || workspaceName.EndsWith(".", StringComparison.Ordinal)
            || workspaceName.Contains("..", StringComparison.Ordinal)
            || IsReservedDeviceName(workspaceName))
        {
            return false;
        }

        return workspaceName.All(character => char.IsLetterOrDigit(character)
            || character is '.' or '_' or '-');
    }

    private static bool IsReservedDeviceName(string workspaceName)
    {
        var baseName = workspaceName.Split('.', 2)[0];
        return ReservedDeviceNames.Contains(baseName)
            || (baseName.Length == 4
                && (baseName.StartsWith("COM", StringComparison.OrdinalIgnoreCase)
                    || baseName.StartsWith("LPT", StringComparison.OrdinalIgnoreCase))
                && (baseName[3] is >= '1' and <= '9'
                    || baseName[3] is '\u00B9' or '\u00B2' or '\u00B3'));
    }
}