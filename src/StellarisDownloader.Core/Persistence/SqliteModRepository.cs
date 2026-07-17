using System.Data;
using System.Globalization;
using Microsoft.Data.Sqlite;
using StellarisDownloader.Core.Models;
using StellarisDownloader.Core.Services;

namespace StellarisDownloader.Core.Persistence;

public sealed class SqliteModRepository : IModRepository, IDisposable
{
    private const int CurrentSchemaVersion = 1;

    private const string CreateSchemaSql = """
        CREATE TABLE IF NOT EXISTS mods (
            workshop_id TEXT NOT NULL PRIMARY KEY,
            app_id INTEGER NOT NULL,
            title TEXT NULL,
            description TEXT NULL,
            preview_url TEXT NULL,
            creator_id TEXT NULL,
            created_at_utc TEXT NULL,
            content_path TEXT NOT NULL,
            file_size INTEGER NULL,
            imported_or_downloaded_at_utc TEXT NOT NULL,
            installed_workshop_updated_at_utc TEXT NULL,
            local_state TEXT NOT NULL,
            last_operation_status TEXT NOT NULL,
            last_error TEXT NULL,
            last_scanned_at_utc TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS cache_state (
            singleton_id INTEGER NOT NULL PRIMARY KEY CHECK (singleton_id = 1),
            schema_version INTEGER NOT NULL,
            library_root TEXT NULL,
            is_stale INTEGER NOT NULL CHECK (is_stale IN (0, 1)),
            last_rebuilt_at_utc TEXT NULL
        );

        INSERT OR IGNORE INTO cache_state (
            singleton_id,
            schema_version,
            library_root,
            is_stale,
            last_rebuilt_at_utc
        ) VALUES (1, 1, NULL, 1, NULL);
        """;

    private const string SelectModsSql = """
        SELECT workshop_id,
               app_id,
               title,
               description,
               preview_url,
               creator_id,
               created_at_utc,
               content_path,
               file_size,
               imported_or_downloaded_at_utc,
               installed_workshop_updated_at_utc,
               local_state,
               last_operation_status,
               last_error,
               last_scanned_at_utc
        FROM mods
        """;

    private const string UpsertModSql = """
        INSERT INTO mods (
            workshop_id,
            app_id,
            title,
            description,
            preview_url,
            creator_id,
            created_at_utc,
            content_path,
            file_size,
            imported_or_downloaded_at_utc,
            installed_workshop_updated_at_utc,
            local_state,
            last_operation_status,
            last_error,
            last_scanned_at_utc
        ) VALUES (
            $workshop_id,
            $app_id,
            $title,
            $description,
            $preview_url,
            $creator_id,
            $created_at_utc,
            $content_path,
            $file_size,
            $imported_or_downloaded_at_utc,
            $installed_workshop_updated_at_utc,
            $local_state,
            $last_operation_status,
            $last_error,
            $last_scanned_at_utc
        )
        ON CONFLICT(workshop_id) DO UPDATE SET
            app_id = excluded.app_id,
            title = excluded.title,
            description = excluded.description,
            preview_url = excluded.preview_url,
            creator_id = excluded.creator_id,
            created_at_utc = excluded.created_at_utc,
            content_path = excluded.content_path,
            file_size = excluded.file_size,
            imported_or_downloaded_at_utc = excluded.imported_or_downloaded_at_utc,
            installed_workshop_updated_at_utc = excluded.installed_workshop_updated_at_utc,
            local_state = excluded.local_state,
            last_operation_status = excluded.last_operation_status,
            last_error = excluded.last_error,
            last_scanned_at_utc = excluded.last_scanned_at_utc;
        """;

    private readonly string databasePath;
    private readonly string connectionString;
    private readonly SemaphoreSlim initializationGate = new(1, 1);
    private volatile bool initialized;

    public SqliteModRepository(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        this.databasePath = Path.GetFullPath(databasePath);
        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = this.databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
        }.ToString();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (initialized)
        {
            return;
        }

        await initializationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (initialized)
            {
                return;
            }

            var directory = Path.GetDirectoryName(databasePath)
                ?? throw new InvalidOperationException("The database directory could not be resolved.");
            Directory.CreateDirectory(directory);

            await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = CreateSchemaSql;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            initialized = true;
        }
        finally
        {
            initializationGate.Release();
        }
    }

    public async Task<IReadOnlyList<ModRecord>> ListAsync(
        string libraryRoot,
        CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        var normalizedRoot = NormalizeLibraryRoot(libraryRoot);

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        if (!await IsUsableForRootAsync(connection, normalizedRoot, cancellationToken).ConfigureAwait(false))
        {
            return [];
        }

        await using var command = connection.CreateCommand();
        command.CommandText = $"{SelectModsSql} ORDER BY workshop_id;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        var records = new List<ModRecord>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            records.Add(ReadMod(reader));
        }

        return records;
    }

    public async Task<ModRecord?> GetAsync(
        string libraryRoot,
        string workshopId,
        CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        var normalizedRoot = NormalizeLibraryRoot(libraryRoot);
        ValidateWorkshopId(workshopId);

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        if (!await IsUsableForRootAsync(connection, normalizedRoot, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        await using var command = connection.CreateCommand();
        command.CommandText = $"{SelectModsSql} WHERE workshop_id = $workshop_id;";
        command.Parameters.AddWithValue("$workshop_id", workshopId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadMod(reader)
            : null;
    }

    public async Task UpsertFinalResultAsync(
        string libraryRoot,
        ModRecord record,
        CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        ArgumentNullException.ThrowIfNull(record);
        var normalizedRoot = NormalizeLibraryRoot(libraryRoot);
        ValidateRecord(record);

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await EnsureUsableForRootAsync(connection, normalizedRoot, cancellationToken).ConfigureAwait(false);
        await UpsertModAsync(connection, transaction: null, record, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> DeleteAsync(
        string libraryRoot,
        string workshopId,
        CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        var normalizedRoot = NormalizeLibraryRoot(libraryRoot);
        ValidateWorkshopId(workshopId);

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await EnsureUsableForRootAsync(connection, normalizedRoot, cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM mods WHERE workshop_id = $workshop_id;";
        command.Parameters.AddWithValue("$workshop_id", workshopId);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
    }

    public async Task ReplaceSnapshotAsync(
        string libraryRoot,
        IReadOnlyCollection<ModRecord> records,
        DateTimeOffset rebuiltAtUtc,
        CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        ArgumentNullException.ThrowIfNull(records);
        var normalizedRoot = NormalizeLibraryRoot(libraryRoot);

        try
        {
            await ReplaceSnapshotCoreAsync(
                normalizedRoot,
                records,
                rebuiltAtUtc,
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await MarkCacheStaleAfterFailureAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task<CacheStateInfo> GetCacheStateAsync(
        string? expectedLibraryRoot,
        CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        var normalizedExpectedRoot = expectedLibraryRoot is null
            ? null
            : NormalizeLibraryRoot(expectedLibraryRoot);

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var stored = await ReadStoredCacheStateAsync(connection, cancellationToken).ConfigureAwait(false);
        var state = stored.IsStale ? CacheState.Stale : CacheState.Valid;

        if (normalizedExpectedRoot is not null
            && !LibraryRootsEqual(normalizedExpectedRoot, stored.LibraryRoot))
        {
            state = CacheState.RootMismatch;
        }

        return new CacheStateInfo(
            stored.SchemaVersion,
            stored.LibraryRoot,
            state,
            stored.LastRebuiltAtUtc);
    }

    public async Task MarkCacheStaleAsync(CancellationToken cancellationToken = default)
    {
        EnsureInitialized();

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await MarkCacheStaleAsync(connection, cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        initializationGate.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task ReplaceSnapshotCoreAsync(
        string normalizedRoot,
        IReadOnlyCollection<ModRecord> records,
        DateTimeOffset rebuiltAtUtc,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken).ConfigureAwait(false);

        var existingRecords = await ReadAllModsAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await using (var deleteCommand = connection.CreateCommand())
        {
            deleteCommand.Transaction = transaction;
            deleteCommand.CommandText = "DELETE FROM mods;";
            await deleteCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var identifiers = new HashSet<string>(StringComparer.Ordinal);
        foreach (var record in records)
        {
            ValidateRecord(record);
            if (!identifiers.Add(record.WorkshopId))
            {
                throw new InvalidDataException($"Duplicate Workshop ID in snapshot: {record.WorkshopId}.");
            }

            var mergedRecord = existingRecords.TryGetValue(record.WorkshopId, out var existingRecord)
                ? PreserveCachedMetadata(record, existingRecord)
                : record;
            await UpsertModAsync(connection, transaction, mergedRecord, cancellationToken).ConfigureAwait(false);
        }

        await using (var stateCommand = connection.CreateCommand())
        {
            stateCommand.Transaction = transaction;
            stateCommand.CommandText = """
                UPDATE cache_state
                SET schema_version = $schema_version,
                    library_root = $library_root,
                    is_stale = 0,
                    last_rebuilt_at_utc = $last_rebuilt_at_utc
                WHERE singleton_id = 1;
                """;
            stateCommand.Parameters.AddWithValue("$schema_version", CurrentSchemaVersion);
            stateCommand.Parameters.AddWithValue("$library_root", normalizedRoot);
            stateCommand.Parameters.AddWithValue("$last_rebuilt_at_utc", FormatTimestamp(rebuiltAtUtc));
            await stateCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<Dictionary<string, ModRecord>> ReadAllModsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = SelectModsSql;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        var records = new Dictionary<string, ModRecord>(StringComparer.Ordinal);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var record = ReadMod(reader);
            records.Add(record.WorkshopId, record);
        }

        return records;
    }

    private async Task MarkCacheStaleAfterFailureAsync()
    {
        try
        {
            await using var connection = await OpenConnectionAsync(CancellationToken.None).ConfigureAwait(false);
            await MarkCacheStaleAsync(connection, CancellationToken.None).ConfigureAwait(false);
        }
        catch (SqliteException)
        {
            // Preserve the original rebuild failure if SQLite cannot record the stale flag.
        }
        catch (IOException)
        {
            // Preserve the original rebuild failure if the database is temporarily unavailable.
        }
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static async Task UpsertModAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        ModRecord record,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = UpsertModSql;
        AddModParameters(command, record);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void AddModParameters(SqliteCommand command, ModRecord record)
    {
        command.Parameters.AddWithValue("$workshop_id", record.WorkshopId);
        command.Parameters.AddWithValue("$app_id", record.AppId);
        command.Parameters.AddWithValue("$title", DatabaseValue(record.Title));
        command.Parameters.AddWithValue("$description", DatabaseValue(record.Description));
        command.Parameters.AddWithValue("$preview_url", DatabaseValue(record.PreviewUrl));
        command.Parameters.AddWithValue("$creator_id", DatabaseValue(record.CreatorId));
        command.Parameters.AddWithValue("$created_at_utc", DatabaseValue(record.CreatedAtUtc));
        command.Parameters.AddWithValue("$content_path", record.ContentPath);
        command.Parameters.AddWithValue("$file_size", DatabaseValue(record.FileSize));
        command.Parameters.AddWithValue(
            "$imported_or_downloaded_at_utc",
            FormatTimestamp(record.ImportedOrDownloadedAtUtc));
        command.Parameters.AddWithValue(
            "$installed_workshop_updated_at_utc",
            DatabaseValue(record.InstalledWorkshopUpdatedAtUtc));
        command.Parameters.AddWithValue("$local_state", record.LocalState.ToString());
        command.Parameters.AddWithValue("$last_operation_status", record.LastOperationStatus.ToString());
        command.Parameters.AddWithValue("$last_error", DatabaseValue(record.LastError));
        command.Parameters.AddWithValue("$last_scanned_at_utc", FormatTimestamp(record.LastScannedAtUtc));
    }

    private static ModRecord ReadMod(SqliteDataReader reader)
    {
        return new ModRecord
        {
            WorkshopId = reader.GetString(0),
            AppId = reader.GetInt32(1),
            Title = ReadNullableString(reader, 2),
            Description = ReadNullableString(reader, 3),
            PreviewUrl = ReadNullableString(reader, 4),
            CreatorId = ReadNullableString(reader, 5),
            CreatedAtUtc = ReadNullableTimestamp(reader, 6),
            ContentPath = reader.GetString(7),
            FileSize = reader.IsDBNull(8) ? null : reader.GetInt64(8),
            ImportedOrDownloadedAtUtc = ParseTimestamp(reader.GetString(9)),
            InstalledWorkshopUpdatedAtUtc = ReadNullableTimestamp(reader, 10),
            LocalState = Enum.Parse<LocalModState>(reader.GetString(11), ignoreCase: false),
            LastOperationStatus = Enum.Parse<OperationStatus>(reader.GetString(12), ignoreCase: false),
            LastError = ReadNullableString(reader, 13),
            LastScannedAtUtc = ParseTimestamp(reader.GetString(14)),
        };
    }

    private static async Task<bool> IsUsableForRootAsync(
        SqliteConnection connection,
        string normalizedRoot,
        CancellationToken cancellationToken)
    {
        var state = await ReadStoredCacheStateAsync(connection, cancellationToken).ConfigureAwait(false);
        return !state.IsStale && LibraryRootsEqual(normalizedRoot, state.LibraryRoot);
    }

    private static async Task EnsureUsableForRootAsync(
        SqliteConnection connection,
        string normalizedRoot,
        CancellationToken cancellationToken)
    {
        if (!await IsUsableForRootAsync(connection, normalizedRoot, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                "The mod cache is stale or belongs to a different library root.");
        }
    }

    private static async Task<StoredCacheState> ReadStoredCacheStateAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT schema_version, library_root, is_stale, last_rebuilt_at_utc
            FROM cache_state
            WHERE singleton_id = 1;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidDataException("The cache state row is missing.");
        }

        return new StoredCacheState(
            reader.GetInt32(0),
            ReadNullableString(reader, 1),
            reader.GetBoolean(2),
            ReadNullableTimestamp(reader, 3));
    }

    private static async Task MarkCacheStaleAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE cache_state SET is_stale = 1 WHERE singleton_id = 1;";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static ModRecord PreserveCachedMetadata(ModRecord incoming, ModRecord cached)
    {
        return incoming with
        {
            Title = incoming.Title ?? cached.Title,
            Description = incoming.Description ?? cached.Description,
            PreviewUrl = incoming.PreviewUrl ?? cached.PreviewUrl,
            CreatorId = incoming.CreatorId ?? cached.CreatorId,
            CreatedAtUtc = incoming.CreatedAtUtc ?? cached.CreatedAtUtc,
        };
    }

    private static string NormalizeLibraryRoot(string libraryRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(libraryRoot);
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(libraryRoot));
    }

    private static bool LibraryRootsEqual(string expected, string? actual)
    {
        return actual is not null
            && string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase);
    }

    private static void ValidateRecord(ModRecord record)
    {
        ValidateWorkshopId(record.WorkshopId);
        ArgumentException.ThrowIfNullOrWhiteSpace(record.ContentPath);

        if (record.AppId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(record), "AppId must be positive.");
        }

        if (record.FileSize < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(record), "FileSize cannot be negative.");
        }
    }

    private static void ValidateWorkshopId(string workshopId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workshopId);
        if (!workshopId.All(char.IsAsciiDigit))
        {
            throw new ArgumentException("Workshop ID must contain only ASCII digits.", nameof(workshopId));
        }
    }

    private static object DatabaseValue(string? value) => value is null ? DBNull.Value : value;

    private static object DatabaseValue(long? value) => value is null ? DBNull.Value : value.Value;

    private static object DatabaseValue(DateTimeOffset? value) => value is null
        ? DBNull.Value
        : FormatTimestamp(value.Value);

    private static string? ReadNullableString(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static DateTimeOffset? ReadNullableTimestamp(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : ParseTimestamp(reader.GetString(ordinal));

    private static string FormatTimestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseTimestamp(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private void EnsureInitialized()
    {
        if (!initialized)
        {
            throw new InvalidOperationException("InitializeAsync must be called before using the repository.");
        }
    }

    private sealed record StoredCacheState(
        int SchemaVersion,
        string? LibraryRoot,
        bool IsStale,
        DateTimeOffset? LastRebuiltAtUtc);
}
