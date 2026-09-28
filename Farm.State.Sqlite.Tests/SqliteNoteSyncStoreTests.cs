using Farm.Core.Cows;
using Xunit;

namespace Farm.State.Sqlite.Tests;

public sealed class SqliteNoteSyncStoreTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"farm-notes-state-{Guid.NewGuid():N}");

    [Fact]
    public async Task Upsert_round_trips_and_overwrites_records()
    {
        var path = Path.Combine(root, "cows.db");
        var synced = DateTimeOffset.Parse("2026-02-03T04:05:06Z");
        var store = new SqliteNoteSyncStore(path);

        await store.UpsertAsync(new NoteSyncRecord(7, "fp-1", "hash-1", synced));
        await store.UpsertAsync(new NoteSyncRecord(7, "fp-2", null, synced.AddHours(1), synced.AddHours(2)));
        await store.UpsertAsync(new NoteSyncRecord(8, "fp-3", "hash-3", synced));

        var records = await new SqliteNoteSyncStore(path).GetAllAsync();
        Assert.Equal(2, records.Count);
        Assert.Equal(new NoteSyncRecord(7, "fp-2", null, synced.AddHours(1), synced.AddHours(2)), records[7]);
        Assert.Equal("hash-3", records[8].NoteHash);
        Assert.Null(records[8].RemovedAt);
    }

    [Fact]
    public async Task Non_positive_work_item_ids_are_rejected()
    {
        var store = new SqliteNoteSyncStore(Path.Combine(root, "cows.db"));

        await Assert.ThrowsAsync<ArgumentException>(() => store.UpsertAsync(new NoteSyncRecord(0, "fp", null, DateTimeOffset.UtcNow)));
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
