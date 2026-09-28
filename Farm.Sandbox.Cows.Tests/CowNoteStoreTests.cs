using Xunit;

namespace Farm.Sandbox.Cows.Tests;

public sealed class CowNoteStoreTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"cow-notes-{Guid.NewGuid():N}");

    [Fact]
    public async Task Atomic_write_replaces_content_leaves_no_temp_files_and_returns_file_hash()
    {
        var store = new CowNoteStore(root);

        await store.WriteAtomicAsync(42, "first");
        var hash = await store.WriteAtomicAsync(42, "second");

        var path = store.GetNotePath(42);
        Assert.Equal("second", await File.ReadAllTextAsync(path));
        Assert.Equal(hash, await store.TryComputeHashAsync(42));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp"));
    }

    [Fact]
    public async Task Hash_is_null_for_missing_note_and_changes_after_local_edit()
    {
        var store = new CowNoteStore(root);
        Assert.Null(await store.TryComputeHashAsync(7));

        var written = await store.WriteAtomicAsync(7, "cloud");
        await File.AppendAllTextAsync(store.GetNotePath(7), " local edit");

        Assert.NotEqual(written, await store.TryComputeHashAsync(7));
    }

    [Fact]
    public async Task Discovery_returns_numeric_folders_with_a_note_only()
    {
        var store = new CowNoteStore(root);
        await store.WriteAtomicAsync(12, "a");
        await store.WriteAtomicAsync(3, "b");
        Directory.CreateDirectory(Path.Combine(root, "99"));
        Directory.CreateDirectory(Path.Combine(root, "feature-slug"));
        await File.WriteAllTextAsync(Path.Combine(Directory.CreateDirectory(Path.Combine(root, "-1")).FullName, CowNoteStore.NoteFileName), "x");

        Assert.Equal([3, 12], store.DiscoverWorkItemIds());
    }

    [Fact]
    public void Note_paths_stay_under_root_and_reject_invalid_ids()
    {
        var store = new CowNoteStore(root);

        Assert.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, store.GetNotePath(5));
        Assert.Throws<ArgumentOutOfRangeException>(() => store.GetNotePath(0));
        Assert.Throws<InvalidOperationException>(() => CowOptions.ValidatePathUnderRoot(Path.Combine("..", "outside.md"), root));
        Assert.Throws<InvalidOperationException>(() => CowOptions.ValidatePathUnderRoot(root, root));
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
