using System.Globalization;
using Farm.Core.Cows;
using Microsoft.Data.Sqlite;

namespace Farm.State.Sqlite;

public sealed class SqliteNoteSyncStore : INoteSyncStore
{
    private readonly string connectionString;

    public SqliteNoteSyncStore(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        var fullPath = Path.GetFullPath(databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        connectionString = new SqliteConnectionStringBuilder { DataSource = fullPath, Pooling = false }.ToString();
        Initialize();
    }

    public async Task<IReadOnlyDictionary<int, NoteSyncRecord>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT work_item_id, fingerprint, note_hash, synced_at, removed_at FROM note_sync;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var records = new Dictionary<int, NoteSyncRecord>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var record = new NoteSyncRecord(
                reader.GetInt32(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                ParseDate(reader.GetString(3)),
                reader.IsDBNull(4) ? null : ParseDate(reader.GetString(4)));
            records[record.WorkItemId] = record;
        }

        return records;
    }

    public async Task UpsertAsync(NoteSyncRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (record.WorkItemId <= 0)
        {
            throw new ArgumentException("Work item ID must be positive.", nameof(record));
        }

        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO note_sync(work_item_id, fingerprint, note_hash, synced_at, removed_at)
            VALUES ($id, $fingerprint, $hash, $synced, $removed)
            ON CONFLICT(work_item_id) DO UPDATE SET
                fingerprint = excluded.fingerprint,
                note_hash = excluded.note_hash,
                synced_at = excluded.synced_at,
                removed_at = excluded.removed_at;
            """;
        command.Parameters.AddWithValue("$id", record.WorkItemId);
        command.Parameters.AddWithValue("$fingerprint", record.Fingerprint);
        command.Parameters.AddWithValue("$hash", (object?)record.NoteHash ?? DBNull.Value);
        command.Parameters.AddWithValue("$synced", record.SyncedAt.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$removed", (object?)record.RemovedAt?.ToString("O", CultureInfo.InvariantCulture) ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static DateTimeOffset ParseDate(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private void Initialize()
    {
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode = WAL;
            CREATE TABLE IF NOT EXISTS note_sync (
                work_item_id INTEGER PRIMARY KEY,
                fingerprint TEXT NOT NULL,
                note_hash TEXT NULL,
                synced_at TEXT NOT NULL,
                removed_at TEXT NULL
            );
            """;
        command.ExecuteNonQuery();
    }
}
