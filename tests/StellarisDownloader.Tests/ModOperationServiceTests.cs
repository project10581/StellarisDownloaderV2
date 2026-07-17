using Microsoft.Data.Sqlite;
using StellarisDownloader.Core.Models;
using StellarisDownloader.Core.Persistence;
using StellarisDownloader.Core.Services;

namespace StellarisDownloader.Tests;

public sealed class ModOperationServiceTests
{
    private static readonly DateTimeOffset InitialTime = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset CompletionTime = new(2026, 7, 17, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task DownloadQueueDeduplicatesRunsSequentiallyAndWritesOneFinalRecordPerSuccess()
    {
        var activeDownloads = 0;
        var maximumConcurrency = 0;
        using var fixture = await ModOperationFixture.CreateAsync(
            async (request, cancellationToken) =>
            {
                var active = Interlocked.Increment(ref activeDownloads);
                maximumConcurrency = Math.Max(maximumConcurrency, active);
                try
                {
                    await Task.Delay(20, cancellationToken);
                    return Success(request);
                }
                finally
                {
                    Interlocked.Decrement(ref activeDownloads);
                }
            },
            metadata: new Dictionary<string, WorkshopMetadata>(StringComparer.Ordinal)
            {
                ["100"] = Metadata("100", "First", CompletionTime.AddDays(-1)),
                ["200"] = Metadata("200", "Second", CompletionTime.AddDays(-2)),
            });
        await ExecuteNonQueryAsync(
            fixture.DatabasePath,
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

        var result = await fixture.Service.DownloadBatchAsync(
            [fixture.Request("100"), fixture.Request("100"), fixture.Request("200")]);

        Assert.Equal(2, result.SucceededCount);
        Assert.Equal(["100", "200"], fixture.SteamCmd.DownloadedIds);
        Assert.Equal(1, maximumConcurrency);
        Assert.Equal(2L, await ExecuteScalarInt64Async(
            fixture.DatabasePath,
            "SELECT write_count FROM write_audit;"));
        var records = await fixture.Repository.ListAsync(fixture.LibraryRoot);
        Assert.Equal(["First", "Second"], records.Select(record => record.Title));
        Assert.All(records, record => Assert.Equal(CompletionTime, record.ImportedOrDownloadedAtUtc));
        Assert.Equal(fixture.LibraryRoot, fixture.Junction.State.TargetPath);
    }

    [Fact]
    public async Task MetadataFailureDoesNotReverseVerifiedFileSuccess()
    {
        using var fixture = await ModOperationFixture.CreateAsync(
            (request, _) => Task.FromResult(Success(request)));

        var result = await fixture.Service.DownloadBatchAsync([fixture.Request("100")]);

        Assert.Equal(OperationStatus.Succeeded, Assert.Single(result.Results).Status);
        var record = await fixture.Repository.GetAsync(fixture.LibraryRoot, "100");
        Assert.NotNull(record);
        Assert.Null(record.Title);
        Assert.Null(record.InstalledWorkshopUpdatedAtUtc);
        Assert.Equal(OperationStatus.Succeeded, record.LastOperationStatus);
    }

    [Fact]
    public async Task InitialFailureWithoutAnExistingRecordDoesNotCreateAPhantomMod()
    {
        using var fixture = await ModOperationFixture.CreateAsync(
            (request, _) => Task.FromResult(Failure(request, "SteamCMD failed.")));

        var result = await fixture.Service.DownloadBatchAsync([fixture.Request("100")]);

        Assert.Equal(OperationStatus.Failed, Assert.Single(result.Results).Status);
        Assert.Null(await fixture.Repository.GetAsync(fixture.LibraryRoot, "100"));
    }

    [Fact]
    public async Task FailedUpdatePreservesTheLastValidRecordAndInstalledSnapshot()
    {
        var installedTime = InitialTime.AddDays(-3);
        var existing = CreateRecord("100", "Cached title", installedTime);
        using var fixture = await ModOperationFixture.CreateAsync(
            (request, _) => Task.FromResult(Failure(request, "Update failed.")),
            [existing]);

        await fixture.Service.UpdateSelectedAsync(fixture.LibraryRoot, ["100"]);

        var record = await fixture.Repository.GetAsync(fixture.LibraryRoot, "100");
        Assert.NotNull(record);
        Assert.Equal("Cached title", record.Title);
        Assert.Equal(installedTime, record.InstalledWorkshopUpdatedAtUtc);
        Assert.Equal(InitialTime, record.ImportedOrDownloadedAtUtc);
        Assert.Equal(OperationStatus.Failed, record.LastOperationStatus);
        Assert.Equal("Update failed.", record.LastError);
    }

    [Fact]
    public async Task SuccessfulUpdateWithMetadataFailurePreservesCachedMetadataAndSnapshot()
    {
        var installedTime = InitialTime.AddDays(-3);
        var existing = CreateRecord("100", "Cached title", installedTime);
        using var fixture = await ModOperationFixture.CreateAsync(
            (request, _) => Task.FromResult(Success(request)),
            [existing]);

        await fixture.Service.UpdateSelectedAsync(fixture.LibraryRoot, ["100"]);

        var record = await fixture.Repository.GetAsync(fixture.LibraryRoot, "100");
        Assert.NotNull(record);
        Assert.Equal("Cached title", record.Title);
        Assert.Equal(installedTime, record.InstalledWorkshopUpdatedAtUtc);
        Assert.Equal(CompletionTime, record.ImportedOrDownloadedAtUtc);
        Assert.Equal(OperationStatus.Succeeded, record.LastOperationStatus);
    }

    [Fact]
    public async Task CancellationStopsCurrentQueueAndMarksUnstartedItemsCancelled()
    {
        using var fixture = await ModOperationFixture.CreateAsync(
            (request, _) => Task.FromResult(request.WorkshopId switch
            {
                "100" => Success(request),
                "200" => Cancelled(request),
                _ => throw new InvalidOperationException("An unstarted queue item reached SteamCMD."),
            }));

        var result = await fixture.Service.DownloadBatchAsync(
            [fixture.Request("100"), fixture.Request("200"), fixture.Request("300")]);

        Assert.Equal(["100", "200"], fixture.SteamCmd.DownloadedIds);
        Assert.Equal(
            [OperationStatus.Succeeded, OperationStatus.Cancelled, OperationStatus.Cancelled],
            result.Results.Select(item => item.Status));
        Assert.NotNull(await fixture.Repository.GetAsync(fixture.LibraryRoot, "100"));
        Assert.Null(await fixture.Repository.GetAsync(fixture.LibraryRoot, "300"));
    }

    [Fact]
    public async Task UpdateCheckUsesInstalledSnapshotThenFallsBackToImportedTimestamp()
    {
        var installed = CreateRecord("100", "Installed", InitialTime.AddDays(-2));
        var imported = CreateRecord("200", "Imported", installedWorkshopUpdatedAtUtc: null) with
        {
            ImportedOrDownloadedAtUtc = InitialTime.AddDays(-1),
        };
        var missing = CreateRecord("300", "Missing metadata", InitialTime);
        using var fixture = await ModOperationFixture.CreateAsync(
            (request, _) => Task.FromResult(Success(request)),
            [installed, imported, missing],
            new Dictionary<string, WorkshopMetadata>(StringComparer.Ordinal)
            {
                ["100"] = Metadata("100", "Installed latest", InitialTime),
                ["200"] = Metadata("200", "Imported latest", InitialTime.AddDays(-2)),
            });

        var results = await fixture.Service.CheckUpdatesAsync(fixture.LibraryRoot);

        Assert.Equal(UpdateState.UpdateAvailable, results.Single(item => item.WorkshopId == "100").State);
        var importedResult = results.Single(item => item.WorkshopId == "200");
        Assert.Equal(UpdateState.UpToDate, importedResult.State);
        Assert.True(importedResult.UsesApproximateLocalTimestamp);
        Assert.Equal(UpdateState.CheckFailed, results.Single(item => item.WorkshopId == "300").State);
    }

    [Fact]
    public async Task UpdateSelectedUsesTheSameDownloadQueueForOnlySelectedIds()
    {
        using var fixture = await ModOperationFixture.CreateAsync(
            (request, _) => Task.FromResult(Success(request)),
            [
                CreateRecord("100", "First", InitialTime),
                CreateRecord("200", "Second", InitialTime),
            ]);

        var result = await fixture.Service.UpdateSelectedAsync(
            fixture.LibraryRoot,
            ["200", "200"]);

        Assert.Equal(1, result.SucceededCount);
        Assert.Equal(["200"], fixture.SteamCmd.DownloadedIds);
    }

    [Fact]
    public async Task StaleCacheBlocksDownloadsBeforeJunctionOrSteamCmd()
    {
        using var fixture = await ModOperationFixture.CreateAsync(
            (request, _) => Task.FromResult(Success(request)));
        await fixture.Repository.MarkCacheStaleAsync();
        fixture.Junction.SetExternalTarget(fixture.TemporaryDirectory.GetPath("other"));

        var result = await fixture.Service.DownloadBatchAsync([fixture.Request("100")]);

        Assert.Equal(OperationStatus.Failed, Assert.Single(result.Results).Status);
        Assert.Empty(fixture.SteamCmd.DownloadedIds);
        Assert.NotEqual(fixture.LibraryRoot, fixture.Junction.State.TargetPath);
    }

    private static DownloadResult Success(DownloadRequest request) => new()
    {
        WorkshopId = request.WorkshopId,
        Status = OperationStatus.Succeeded,
        ContentPath = Path.Combine(request.LibraryRoot, request.WorkshopId),
        FolderExists = true,
        FolderNonEmpty = true,
    };

    private static DownloadResult Failure(DownloadRequest request, string error) => new()
    {
        WorkshopId = request.WorkshopId,
        Status = OperationStatus.Failed,
        ContentPath = Path.Combine(request.LibraryRoot, request.WorkshopId),
        Error = error,
    };

    private static DownloadResult Cancelled(DownloadRequest request) => new()
    {
        WorkshopId = request.WorkshopId,
        Status = OperationStatus.Cancelled,
        ContentPath = Path.Combine(request.LibraryRoot, request.WorkshopId),
        Error = "Cancelled.",
    };

    private static WorkshopMetadata Metadata(
        string workshopId,
        string title,
        DateTimeOffset updatedAtUtc) => new()
        {
            WorkshopId = workshopId,
            Title = title,
            LatestRemoteUpdatedAtUtc = updatedAtUtc,
        };

    private static ModRecord CreateRecord(
        string workshopId,
        string title,
        DateTimeOffset? installedWorkshopUpdatedAtUtc) => new()
        {
            WorkshopId = workshopId,
            Title = title,
            ContentPath = workshopId,
            ImportedOrDownloadedAtUtc = InitialTime,
            InstalledWorkshopUpdatedAtUtc = installedWorkshopUpdatedAtUtc,
            LastScannedAtUtc = InitialTime,
        };

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
        await using var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        return Assert.IsType<long>(await command.ExecuteScalarAsync());
    }

    private sealed class ModOperationFixture : IDisposable
    {
        private readonly WriteOperationCoordinator coordinator;

        private ModOperationFixture(
            TemporaryDirectory temporaryDirectory,
            SqliteModRepository repository,
            StubSteamCmdService steamCmd,
            StubWorkshopClient workshop,
            MemoryJunctionManager junction,
            WriteOperationCoordinator coordinator,
            ModOperationService service,
            string libraryRoot,
            string databasePath)
        {
            TemporaryDirectory = temporaryDirectory;
            Repository = repository;
            SteamCmd = steamCmd;
            Workshop = workshop;
            Junction = junction;
            this.coordinator = coordinator;
            Service = service;
            LibraryRoot = libraryRoot;
            DatabasePath = databasePath;
        }

        public TemporaryDirectory TemporaryDirectory { get; }

        public SqliteModRepository Repository { get; }

        public StubSteamCmdService SteamCmd { get; }

        public StubWorkshopClient Workshop { get; }

        public MemoryJunctionManager Junction { get; }

        public ModOperationService Service { get; }

        public string LibraryRoot { get; }

        public string DatabasePath { get; }

        public static async Task<ModOperationFixture> CreateAsync(
            Func<DownloadRequest, CancellationToken, Task<DownloadResult>> download,
            IReadOnlyCollection<ModRecord>? initialRecords = null,
            IReadOnlyDictionary<string, WorkshopMetadata>? metadata = null)
        {
            var temporaryDirectory = new TemporaryDirectory();
            var libraryRoot = temporaryDirectory.GetPath("library");
            Directory.CreateDirectory(libraryRoot);
            var databasePath = temporaryDirectory.GetPath("library.db");
            var repository = new SqliteModRepository(databasePath);
            await repository.InitializeAsync();
            var records = (initialRecords ?? []).Select(record => record with
            {
                ContentPath = Path.Combine(libraryRoot, record.WorkshopId),
            }).ToArray();
            await repository.ReplaceSnapshotAsync(libraryRoot, records, InitialTime);
            var steamCmd = new StubSteamCmdService(download);
            var workshop = new StubWorkshopClient(metadata);
            var junctionPath = temporaryDirectory.GetPath(
                Path.Combine("steamcmd", "steamapps", "workshop", "content", "281990"));
            var junction = new MemoryJunctionManager(junctionPath, libraryRoot);
            var coordinator = new WriteOperationCoordinator();
            var service = new ModOperationService(
                repository,
                steamCmd,
                workshop,
                junction,
                coordinator,
                junctionPath,
                new FixedTimeProvider(CompletionTime));
            return new ModOperationFixture(
                temporaryDirectory,
                repository,
                steamCmd,
                workshop,
                junction,
                coordinator,
                service,
                libraryRoot,
                databasePath);
        }

        public DownloadRequest Request(string workshopId) => new()
        {
            WorkshopId = workshopId,
            LibraryRoot = LibraryRoot,
        };

        public void Dispose()
        {
            coordinator.Dispose();
            Repository.Dispose();
            TemporaryDirectory.Dispose();
            GC.SuppressFinalize(this);
        }
    }
}
