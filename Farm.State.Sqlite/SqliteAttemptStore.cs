using System.Text.Json;
using Farm.Core.Chickens;
using Microsoft.Data.Sqlite;

namespace Farm.State.Sqlite;

public sealed class SqliteAttemptStore : IAttemptStore
{
    private readonly string connectionString;

    public SqliteAttemptStore(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        var fullPath = Path.GetFullPath(databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        connectionString = new SqliteConnectionStringBuilder { DataSource = fullPath, Pooling = false }.ToString();
        Initialize();
    }

    public async Task<bool> HasCompletedAttemptAsync(AttemptKey key, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM attempts WHERE attempt_key = $key AND outcome IN ('ChangesProduced', 'ExplanationOnly'));";
        command.Parameters.AddWithValue("$key", SerializeKey(key));
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken)) == 1;
    }

    public async Task<Lease?> TryAcquireLeaseAsync(
        AttemptKey key,
        string ownerId,
        TimeSpan duration,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        if (duration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(duration));
        }

        var now = DateTimeOffset.UtcNow;
        var expiresAt = now.Add(duration);
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        await using (var completed = connection.CreateCommand())
        {
            completed.Transaction = transaction;
            completed.CommandText = "SELECT EXISTS(SELECT 1 FROM attempts WHERE attempt_key = $key AND outcome IN ('ChangesProduced', 'ExplanationOnly'));";
            completed.Parameters.AddWithValue("$key", SerializeKey(key));
            if (Convert.ToInt64(await completed.ExecuteScalarAsync(cancellationToken)) == 1)
            {
                await transaction.CommitAsync(cancellationToken);
                return null;
            }
        }

        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM leases WHERE expires_at <= $now;";
            delete.Parameters.AddWithValue("$now", now.ToString("O"));
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = "INSERT OR IGNORE INTO leases(attempt_key, owner_id, expires_at) VALUES ($key, $owner, $expires);";
        insert.Parameters.AddWithValue("$key", SerializeKey(key));
        insert.Parameters.AddWithValue("$owner", ownerId);
        insert.Parameters.AddWithValue("$expires", expiresAt.ToString("O"));
        var acquired = await insert.ExecuteNonQueryAsync(cancellationToken) == 1;
        await transaction.CommitAsync(cancellationToken);
        return acquired ? new Lease(ownerId, key, expiresAt) : null;
    }

    public async Task<Lease?> RenewLeaseAsync(
        Lease lease,
        TimeSpan duration,
        CancellationToken cancellationToken = default)
    {
        if (duration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(duration));
        }

        var now = DateTimeOffset.UtcNow;
        var expiresAt = now.Add(duration);
        await using var connection = await OpenAsync(cancellationToken);
        await using var update = connection.CreateCommand();
        update.CommandText = """
            UPDATE leases SET expires_at = $expires
            WHERE attempt_key = $key AND owner_id = $owner AND expires_at > $now;
            """;
        update.Parameters.AddWithValue("$expires", expiresAt.ToString("O"));
        update.Parameters.AddWithValue("$key", SerializeKey(lease.Key));
        update.Parameters.AddWithValue("$owner", lease.OwnerId);
        update.Parameters.AddWithValue("$now", now.ToString("O"));
        return await update.ExecuteNonQueryAsync(cancellationToken) == 1
            ? lease with { ExpiresAt = expiresAt }
            : null;
    }

    public async Task CompleteAsync(Lease lease, AttemptState attempt, CancellationToken cancellationToken = default)
    {
        if (lease.Key != attempt.Key)
        {
            throw new ArgumentException("Lease and attempt keys must match.", nameof(attempt));
        }

        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;
        await using (var verify = connection.CreateCommand())
        {
            verify.Transaction = transaction;
            verify.CommandText = """
                SELECT EXISTS(SELECT 1 FROM leases
                WHERE attempt_key = $key AND owner_id = $owner AND expires_at > $now);
                """;
            verify.Parameters.AddWithValue("$key", SerializeKey(lease.Key));
            verify.Parameters.AddWithValue("$owner", lease.OwnerId);
            verify.Parameters.AddWithValue("$now", now.ToString("O"));
            if (Convert.ToInt64(await verify.ExecuteScalarAsync(cancellationToken)) != 1)
            {
                throw new InvalidOperationException("The attempt cannot be completed without its current unexpired lease.");
            }
        }

        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT OR IGNORE INTO attempts(attempt_key, outcome, completed_at, artifact_directory, work_item_fingerprint, work_item_hashes)
                VALUES ($key, $outcome, $completed, $artifacts, $workItemFingerprint, $workItemHashes);
                """;
            insert.Parameters.AddWithValue("$key", SerializeKey(attempt.Key));
            insert.Parameters.AddWithValue("$outcome", attempt.Outcome.ToString());
            insert.Parameters.AddWithValue("$completed", attempt.CompletedAt.ToString("O"));
            insert.Parameters.AddWithValue("$artifacts", attempt.ArtifactDirectory);
            insert.Parameters.AddWithValue("$workItemFingerprint", (object?)attempt.Key.WorkItemFingerprint ?? DBNull.Value);
            insert.Parameters.AddWithValue("$workItemHashes", SerializeHashes(attempt.WorkItemHashes));
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await DeleteLeaseAsync(connection, transaction, lease, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<AttemptBaseline?> GetBaselineAsync(AttemptKey key, CancellationToken cancellationToken = default)
    {
        var legacy = key with { WorkItemFingerprint = null };
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT work_item_fingerprint, work_item_hashes FROM attempts
            WHERE outcome IN ('ChangesProduced', 'ExplanationOnly')
              AND (attempt_key = $legacy OR substr(attempt_key, 1, length($prefix)) = $prefix)
            ORDER BY work_item_fingerprint IS NULL, completed_at DESC, rowid DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$legacy", SerializeKey(legacy));
        command.Parameters.AddWithValue("$prefix", SerializeKey(legacy) + KeySeparator);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new AttemptBaseline(
            reader.IsDBNull(0) ? null : reader.GetString(0),
            reader.IsDBNull(1) ? null : JsonSerializer.Deserialize<Dictionary<int, string>>(reader.GetString(1)));
    }

    public async Task<bool> AdoptLegacyCompletionAsync(
        AttemptKey key,
        IReadOnlyDictionary<int, string> workItemHashes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workItemHashes);
        if (key.WorkItemFingerprint is null)
        {
            throw new ArgumentException("A work item fingerprint is required to adopt a legacy completion.", nameof(key));
        }

        var legacy = key with { WorkItemFingerprint = null };
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR IGNORE INTO attempts(attempt_key, outcome, completed_at, artifact_directory, work_item_fingerprint, work_item_hashes)
            SELECT $key, outcome, $completed, artifact_directory, $workItemFingerprint, $workItemHashes FROM attempts
            WHERE attempt_key = $legacy AND outcome IN ('ChangesProduced', 'ExplanationOnly')
              AND NOT EXISTS (SELECT 1 FROM attempts WHERE substr(attempt_key, 1, length($prefix)) = $prefix);
            """;
        command.Parameters.AddWithValue("$key", SerializeKey(key));
        command.Parameters.AddWithValue("$completed", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$workItemFingerprint", key.WorkItemFingerprint);
        command.Parameters.AddWithValue("$workItemHashes", SerializeHashes(workItemHashes));
        command.Parameters.AddWithValue("$legacy", SerializeKey(legacy));
        command.Parameters.AddWithValue("$prefix", SerializeKey(legacy) + KeySeparator);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task ReleaseAsync(Lease lease, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await DeleteLeaseAsync(connection, transaction, lease, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static async Task DeleteLeaseAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Lease lease,
        CancellationToken cancellationToken)
    {
        await using var delete = connection.CreateCommand();
        delete.Transaction = transaction;
        delete.CommandText = "DELETE FROM leases WHERE attempt_key = $key AND owner_id = $owner;";
        delete.Parameters.AddWithValue("$key", SerializeKey(lease.Key));
        delete.Parameters.AddWithValue("$owner", lease.OwnerId);
        await delete.ExecuteNonQueryAsync(cancellationToken);
    }

    private void Initialize()
    {
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode = WAL;
            CREATE TABLE IF NOT EXISTS attempts (
                attempt_key TEXT PRIMARY KEY,
                outcome TEXT NOT NULL,
                completed_at TEXT NOT NULL,
                artifact_directory TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS leases (
                attempt_key TEXT PRIMARY KEY,
                owner_id TEXT NOT NULL,
                expires_at TEXT NOT NULL
            );
            """;
        command.ExecuteNonQuery();

        using var columns = connection.CreateCommand();
        columns.CommandText = "SELECT name FROM pragma_table_info('attempts');";
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var reader = columns.ExecuteReader())
        {
            while (reader.Read())
            {
                existing.Add(reader.GetString(0));
            }
        }

        foreach (var column in new[] { "work_item_fingerprint", "work_item_hashes" }.Where(name => !existing.Contains(name)))
        {
            using var alter = connection.CreateCommand();
            alter.CommandText = $"ALTER TABLE attempts ADD COLUMN {column} TEXT NULL;";
            alter.ExecuteNonQuery();
        }
    }

    private const char KeySeparator = '\u001f';

    // Keys without a work item component serialize exactly as before, so legacy rows stay addressable.
    private static string SerializeKey(AttemptKey key)
    {
        var legacy = string.Join(KeySeparator,
            key.Organization, key.Project, key.RepositoryId, key.PullRequestId, key.HeadSha, key.FeedbackFingerprint);
        return key.WorkItemFingerprint is null ? legacy : legacy + KeySeparator + key.WorkItemFingerprint;
    }

    private static object SerializeHashes(IReadOnlyDictionary<int, string>? hashes) =>
        hashes is null ? DBNull.Value : JsonSerializer.Serialize(hashes);
}