namespace Farm.Sandbox.Chickens;

public sealed class ProcessLock : IDisposable
{
    private readonly FileStream stream;

    public ProcessLock(string path)
    {
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        try
        {
            stream = new FileStream(fullPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException exception)
        {
            throw new InvalidOperationException($"Another Farm.Sandbox.Chickens process holds lock '{fullPath}'.", exception);
        }
    }

    public void Dispose() => stream.Dispose();
}