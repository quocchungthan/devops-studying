using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Farm.Copilot;

namespace Farm.Sandbox.Cows;

// Notes live at <root>/<work-item-id>/README.md, mirroring the skill's docs/investigations/<issue-id>/README.md layout.
public sealed class CowNoteStore
{
    public const string NoteFileName = "README.md";

    private readonly string root;

    public CowNoteStore(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        root = Path.GetFullPath(rootPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    public string RootPath => root;

    public string GetNotePath(int workItemId)
    {
        if (workItemId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(workItemId), "Work item ID must be positive.");
        }

        var path = CowOptions.ValidatePathUnderRoot(
            Path.Combine(workItemId.ToString(CultureInfo.InvariantCulture), NoteFileName), root);
        if (!CopilotSafetyPolicy.IsConfinedPath(path, root))
        {
            throw new InvalidOperationException($"Note path for work item {workItemId} resolves outside the notes root.");
        }

        return path;
    }

    public IReadOnlyList<int> DiscoverWorkItemIds()
    {
        if (!Directory.Exists(root))
        {
            return [];
        }

        return Directory.EnumerateDirectories(root)
            .Select(Path.GetFileName)
            .Select(name => int.TryParse(name, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0 ? id : 0)
            .Where(id => id > 0 && File.Exists(Path.Combine(root, id.ToString(CultureInfo.InvariantCulture), NoteFileName)))
            .Order()
            .ToArray();
    }

    public async Task<string?> TryComputeHashAsync(int workItemId, CancellationToken cancellationToken = default)
    {
        var path = GetNotePath(workItemId);
        if (!File.Exists(path))
        {
            return null;
        }

        await using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken));
    }

    public async Task<string> WriteAtomicAsync(int workItemId, string content, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        var path = GetNotePath(workItemId);
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var bytes = Encoding.UTF8.GetBytes(content);
        var temporaryPath = Path.Combine(directory, $".{NoteFileName}.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllBytesAsync(temporaryPath, bytes, cancellationToken);
            File.Move(temporaryPath, path, true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }

        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }
}
