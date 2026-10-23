using System.Globalization;
using Microsoft.Data.Sqlite;
using StellarisDownloader.Core.Models;
using StellarisDownloader.Core.Persistence;

namespace StellarisDownloader.Tests;

public sealed class SqliteModRepositoryTests
{
    private static readonly DateTimeOffset TestTime = new(2026, 7, 16, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task InitializeIsIdempotentAndCreatesOneCacheStateRow()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var databasePath = temporaryDirectory.GetPath("library.db");
        using var repository = new SqliteModRepository(databasePath);

        await repository.InitializeAsync();
        await repository.InitializeAsync();

        Assert.Equal(1L, await ExecuteScalarInt64Async(databasePath, "SELECT COUNT(*) FROM cache_state;"));
        var state = await repository.GetCacheStateAsync(expectedLibraryRoot: null);
        Assert.Equal(CacheState.Stale, state.State);
        Assert.Equal(2, state.SchemaVersion);
    }

    [Fact]
    public async Task SnapshotCrudAndFinalUpsertUseTheBoundLibraryRoot()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var databasePath = temporaryDirectory.GetPath("library.db");
        var root = temporaryDirectory.GetPath("library");
        using var repository = new SqliteModRepository(databasePath);
        await repository.InitializeAsync();
        var first = CreateRecord(root, "100", "First") with
        {
            CreatorId = "42",
            CreatorName = "First Author",
        };

        await repository.ReplaceSnapshotAsync(root, [first], TestTime);

        Assert.Equal(CacheState.Valid, (await repository.GetCacheStateAsync(root)).State);
        Assert.Equal(first, await repository.GetAsync(root, first.WorkshopId));
        Assert.Single(await repository.ListAsync(root));

        var updated = first with
        {
            Title = "Updated",
            LastOperationStatus = OperationStatus.Failed,
            LastError = "test failure",
        };
        await repository.UpsertFinalResultAsync(root, updated);

        Assert.Equal(updated, await repository.GetAsync(root, updated.WorkshopId));
        Assert.True(await repository.DeleteAsync(root, updated.WorkshopId));
        Assert.False(await repository.DeleteAsync(root, updated.WorkshopId));
        Assert.Empty(await repository.ListAsync(root));
    }

    [Fact]
    public async Task RootMismatchHidesRecordsAndBlocksMutations()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var databasePath = temporaryDirectory.GetPath("library.db");
        var firstRoot = temporaryDirectory.GetPath("first");
        var secondRoot = temporaryDirectory.GetPath("second");
        using var repository = new SqliteModRepository(databasePath);
        await repository.InitializeAsync();
        await repository.ReplaceSnapshotAsync(firstRoot, [CreateRecord(firstRoot, "100", "First")], TestTime);

        Assert.Equal(CacheState.RootMismatch, (await repository.GetCacheStateAsync(secondRoot)).State);
        Assert.Empty(await repository.ListAsync(secondRoot));
        Assert.Null(await repository.GetAsync(secondRoot, "100"));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => repository.UpsertFinalResultAsync(secondRoot, CreateRecord(secondRoot, "200", "Second")));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => repository.DeleteAsync(secondRoot, "100"));
    }

    [Fact]
    public async Task StaleSameRootSnapshotPreservesCachedMetadataAndInstalledState()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var databasePath = temporaryDirectory.GetPath("library.db");
        var root = temporaryDirectory.GetPath("library");
        using var repository = new SqliteModRepository(databasePath);
        await repository.InitializeAsync();
        var cached = CreateRecord(root, "100", "Cached title") with
        {
            Description = "Cached description",
            PreviewUrl = "https://example.invalid/preview.png",
            CreatorId = "42",
            CreatorName = "Cached Author",
            CreatedAtUtc = TestTime.AddYears(-1),
            FileSize = 456,
            ImportedOrDownloadedAtUtc = TestTime.AddDays(-10),
            InstalledWorkshopUpdatedAtUtc = TestTime.AddDays(-5),
            LastOperationStatus = OperationStatus.Failed,
            LastError = "Previous update failed.",
        };
        await repository.ReplaceSnapshotAsync(root, [cached], TestTime);
        var scanned = CreateRecord(root, "100", title: null) with
        {
            Description = null,
            PreviewUrl = null,
            CreatorId = null,
            CreatorName = null,
            CreatedAtUtc = null,
            FileSize = null,
            ImportedOrDownloadedAtUtc = TestTime.AddHours(1),
            InstalledWorkshopUpdatedAtUtc = null,
            LastOperationStatus = OperationStatus.Succeeded,
            LastError = null,
            LastScannedAtUtc = TestTime.AddHours(1),
        };
        await repository.MarkCacheStaleAsync();

        await repository.ReplaceSnapshotAsync(root, [scanned], TestTime.AddHours(1));

        var result = await repository.GetAsync(root, "100");
        Assert.NotNull(result);
        Assert.Equal(cached.Title, result.Title);
        Assert.Equal(cached.Description, result.Description);
        Assert.Equal(cached.PreviewUrl, result.PreviewUrl);
        Assert.Equal(cached.CreatorId, result.CreatorId);
        Assert.Equal(cached.CreatorName, result.CreatorName);
        Assert.Equal(cached.CreatedAtUtc, result.CreatedAtUtc);
        Assert.Equal(cached.FileSize, result.FileSize);
        Assert.Equal(cached.ImportedOrDownloadedAtUtc, result.ImportedOrDownloadedAtUtc);
        Assert.Equal(cached.InstalledWorkshopUpdatedAtUtc, result.InstalledWorkshopUpdatedAtUtc);
        Assert.Equal(cached.LastOperationStatus, result.LastOperationStatus);
        Assert.Equal(cached.LastError, result.LastError);
        Assert.Equal(scanned.LastScannedAtUtc, result.LastScannedAtUtc);
    }

    [Fact]
    public async Task CrossRootSnapshotPreservesOnlyCachedMetadata()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var databasePath = temporaryDirectory.GetPath("library.db");
        var oldRoot = temporaryDirectory.GetPath("old-library");
        var newRoot = temporaryDirectory.GetPath("new-library");
        using var repository = new SqliteModRepository(databasePath);
        await repository.InitializeAsync();
        var cached = CreateRecord(oldRoot, "100", "Cached title") with
        {
            Description = "Cached description",
            CreatorId = "42",
            CreatorName = "Cached Author",
            FileSize = 456,
            ImportedOrDownloadedAtUtc = TestTime.AddDays(-10),
            InstalledWorkshopUpdatedAtUtc = TestTime.AddDays(-5),
            LastOperationStatus = OperationStatus.Failed,
            LastError = "Old library failure.",
        };
        await repository.ReplaceSnapshotAsync(oldRoot, [cached], TestTime);
        var importedAtUtc = TestTime.AddHours(2);
        var scanned = CreateRecord(newRoot, "100", title: null) with
        {
            Description = null,
            CreatorId = null,
            CreatorName = null,
            FileSize = null,
            ImportedOrDownloadedAtUtc = importedAtUtc,
            InstalledWorkshopUpdatedAtUtc = null,
            LastOperationStatus = OperationStatus.Succeeded,
            LastError = null,
            LastScannedAtUtc = TestTime.AddHours(2),
        };
        await repository.MarkCacheStaleAsync();

        await repository.ReplaceSnapshotAsync(newRoot, [scanned], TestTime.AddHours(2));

        var result = await repository.GetAsync(newRoot, "100");
        Assert.NotNull(result);
        Assert.Equal(cached.Title, result.Title);
        Assert.Equal(cached.Description, result.Description);
        Assert.Equal(cached.CreatorId, result.CreatorId);
        Assert.Equal(cached.CreatorName, result.CreatorName);
        Assert.Equal(cached.FileSize, result.FileSize);
        Assert.Equal(importedAtUtc, result.ImportedOrDownloadedAtUtc);
        Assert.Null(result.InstalledWorkshopUpdatedAtUtc);
        Assert.Equal(OperationStatus.Succeeded, result.LastOperationStatus);
        Assert.Null(result.LastError);
        Assert.Equal(Path.Combine(newRoot, "100"), result.ContentPath);
    }

    [Fact]
    public async Task SnapshotDoesNotReuseAuthorNameAfterCreatorChanges()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var databasePath = temporaryDirectory.GetPath("library.db");
        var root = temporaryDirectory.GetPath("library");
        using var repository = new SqliteModRepository(databasePath);
        await repository.InitializeAsync();
        var cached = CreateRecord(root, "100", "Cached") with
        {
            CreatorId = "76561198000000001",
            CreatorName = "Old Author",
        };
        await repository.ReplaceSnapshotAsync(root, [cached], TestTime);
        var refreshed = CreateRecord(root, "100", "Refreshed") with
        {
            CreatorId = "76561198000000002",
            CreatorName = null,
        };

        await repository.ReplaceSnapshotAsync(root, [refreshed], TestTime.AddMinutes(1));

        var result = await repository.GetAsync(root, "100");
        Assert.NotNull(result);
        Assert.Equal("76561198000000002", result.CreatorId);
        Assert.Null(result.CreatorName);
    }

    [Fact]
    public async Task FailedSnapshotRollsBackOldRowsAndMarksCacheStale()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var databasePath = temporaryDirectory.GetPath("library.db");
        var root = temporaryDirectory.GetPath("library");
        using var repository = new SqliteModRepository(databasePath);
        await repository.InitializeAsync();
        await repository.ReplaceSnapshotAsync(root, [CreateRecord(root, "100", "Original")], TestTime);
        await ExecuteNonQueryAsync(
            databasePath,
            """
            CREATE TRIGGER force_snapshot_failure
            BEFORE INSERT ON mods
            WHEN NEW.workshop_id = '999'
            BEGIN
                SELECT RAISE(ABORT, 'forced snapshot failure');
            END;
            """);

        await Assert.ThrowsAsync<SqliteException>(
            () => repository.ReplaceSnapshotAsync(
                root,
                [CreateRecord(root, "200", "New"), CreateRecord(root, "999", "Failure")],
                TestTime.AddHours(1)));

        Assert.Equal(CacheState.Stale, (await repository.GetCacheStateAsync(root)).State);
        Assert.Empty(await repository.ListAsync(root));
        Assert.Equal(1L, await ExecuteScalarInt64Async(databasePath, "SELECT COUNT(*) FROM mods;"));
        Assert.Equal(
            "Original",
            await ExecuteScalarStringAsync(databasePath, "SELECT title FROM mods WHERE workshop_id = '100';"));
    }

    [Fact]
    public async Task FinalUpsertPerformsOneDatabaseWrite()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var databasePath = temporaryDirectory.GetPath("library.db");
        var root = temporaryDirectory.GetPath("library");
        using var repository = new SqliteModRepository(databasePath);
        await repository.InitializeAsync();
        await repository.ReplaceSnapshotAsync(root, [], TestTime);
        await ExecuteNonQueryAsync(
            databasePath,
            """
            CREATE TABLE write_audit (write_count INTEGER NOT NULL);
            INSERT INTO write_audit (write_count) VALUES (0);
            CREATE TRIGGER count_mod_insert AFTER INSERT ON mods
            BEGIN
                UPDATE write_audit SET write_count = write_count + 1;
            END;
            CREATE TRIGGER count_mod_update AFTER UPDATE ON mods
            BEGIN
                UPDATE write_audit SET write_count = write_count + 1;
            END;
            """);

        await repository.UpsertFinalResultAsync(root, CreateRecord(root, "100", "Final"));

        Assert.Equal(1L, await ExecuteScalarInt64Async(databasePath, "SELECT write_count FROM write_audit;"));
    }

    [Fact]
    public async Task InitializeMigratesVersionOneDatabaseWithoutLosingRows()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var databasePath = temporaryDirectory.GetPath("library.db");
        var root = temporaryDirectory.GetPath("library");
        await CreateVersionOneDatabaseAsync(databasePath, root);
        using var repository = new SqliteModRepository(databasePath);

        await repository.InitializeAsync();
        await repository.InitializeAsync();

        var state = await repository.GetCacheStateAsync(root);
        Assert.Equal(2, state.SchemaVersion);
        Assert.Equal(CacheState.Valid, state.State);
        Assert.Equal(
            1L,
            await ExecuteScalarInt64Async(
                databasePath,
                "SELECT COUNT(*) FROM pragma_table_info('mods') WHERE name = 'creator_name';"));
        var migrated = await repository.GetAsync(root, "100");
        Assert.NotNull(migrated);
        Assert.Equal("Version one title", migrated.Title);
        Assert.Equal("76561198000000001", migrated.CreatorId);
        Assert.Null(migrated.CreatorName);

        var enriched = migrated with { CreatorName = "Migrated Author" };
        await repository.UpsertFinalResultAsync(root, enriched);

        Assert.Equal(enriched, await repository.GetAsync(root, "100"));
    }

    private static ModRecord CreateRecord(string root, string workshopId, string? title)
    {
        return new ModRecord
        {
            WorkshopId = workshopId,
            Title = title,
            ContentPath = Path.Combine(root, workshopId),
            FileSize = 123,
            ImportedOrDownloadedAtUtc = TestTime,
            InstalledWorkshopUpdatedAtUtc = TestTime.AddMinutes(-1),
            LastScannedAtUtc = TestTime,
        };
    }

    private static async Task ExecuteNonQueryAsync(string databasePath, string commandText)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task CreateVersionOneDatabaseAsync(string databasePath, string root)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE mods (
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

            CREATE TABLE cache_state (
                singleton_id INTEGER NOT NULL PRIMARY KEY CHECK (singleton_id = 1),
                schema_version INTEGER NOT NULL,
                library_root TEXT NULL,
                is_stale INTEGER NOT NULL CHECK (is_stale IN (0, 1)),
                last_rebuilt_at_utc TEXT NULL
            );

            INSERT INTO mods (
                workshop_id,
                app_id,
                title,
                creator_id,
                content_path,
                imported_or_downloaded_at_utc,
                local_state,
                last_operation_status,
                last_scanned_at_utc
            ) VALUES (
                '100',
                281990,
                'Version one title',
                '76561198000000001',
                $content_path,
                $timestamp,
                'Available',
                'Succeeded',
                $timestamp
            );

            INSERT INTO cache_state (
                singleton_id,
                schema_version,
                library_root,
                is_stale,
                last_rebuilt_at_utc
            ) VALUES (1, 1, $library_root, 0, $timestamp);
            """;
        command.Parameters.AddWithValue("$content_path", Path.Combine(root, "100"));
        command.Parameters.AddWithValue("$library_root", Path.GetFullPath(root));
        command.Parameters.AddWithValue("$timestamp", TestTime.ToString("O", CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> ExecuteScalarInt64Async(string databasePath, string commandText)
    {
        var result = await ExecuteScalarAsync(databasePath, commandText);
        return Assert.IsType<long>(result);
    }

    private static async Task<string> ExecuteScalarStringAsync(string databasePath, string commandText)
    {
        var result = await ExecuteScalarAsync(databasePath, commandText);
        return Assert.IsType<string>(result);
    }

    private static async Task<object?> ExecuteScalarAsync(string databasePath, string commandText)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        return await command.ExecuteScalarAsync();
    }
}
