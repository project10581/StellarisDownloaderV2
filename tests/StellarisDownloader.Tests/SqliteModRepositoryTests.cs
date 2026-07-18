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
        Assert.Equal(1, state.SchemaVersion);
    }

    [Fact]
    public async Task SnapshotCrudAndFinalUpsertUseTheBoundLibraryRoot()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var databasePath = temporaryDirectory.GetPath("library.db");
        var root = temporaryDirectory.GetPath("library");
        using var repository = new SqliteModRepository(databasePath);
        await repository.InitializeAsync();
        var first = CreateRecord(root, "100", "First");

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
        Assert.Equal(cached.FileSize, result.FileSize);
        Assert.Equal(importedAtUtc, result.ImportedOrDownloadedAtUtc);
        Assert.Null(result.InstalledWorkshopUpdatedAtUtc);
        Assert.Equal(OperationStatus.Succeeded, result.LastOperationStatus);
        Assert.Null(result.LastError);
        Assert.Equal(Path.Combine(newRoot, "100"), result.ContentPath);
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
