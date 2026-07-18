using StellarisDownloader.Core.Models;
using StellarisDownloader.Core.Persistence;
using StellarisDownloader.Core.Services;

namespace StellarisDownloader.Tests;

public sealed class LibraryServiceTests
{
    private static readonly DateTimeOffset TestTime = new(2026, 7, 16, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ScanImportsOnlyDirectNonEmptyAsciiNumericDirectoriesAndSummarizesChanges()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var root = temporaryDirectory.GetPath("library");
        Directory.CreateDirectory(root);
        await CreateNonEmptyDirectoryAsync(root, "100");
        await CreateNonEmptyDirectoryAsync(root, "400");
        Directory.CreateDirectory(Path.Combine(root, "300"));
        await CreateNonEmptyDirectoryAsync(root, "not-a-mod");
        await CreateNonEmptyDirectoryAsync(Path.Combine(root, "100"), "500");
        await File.WriteAllTextAsync(Path.Combine(root, "600"), "a file is not a mod directory");
        var oldTimestamp = new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        Directory.SetLastWriteTimeUtc(Path.Combine(root, "100"), oldTimestamp);
        var newImportTimestamp = oldTimestamp.AddDays(1);
        Directory.SetLastWriteTimeUtc(Path.Combine(root, "400"), newImportTimestamp);

        var databasePath = temporaryDirectory.GetPath("library.db");
        using var repository = new SqliteModRepository(databasePath);
        await repository.InitializeAsync();
        await repository.ReplaceSnapshotAsync(
            root,
            [CreateRecord(root, "100"), CreateRecord(root, "200")],
            TestTime);
        var settings = new StubSettingsStore(new AppSettings { LibraryRoot = root });
        var junctionPath = temporaryDirectory.GetPath(Path.Combine("steamcmd", "281990"));
        var junction = new MemoryJunctionManager(junctionPath, root);
        var workshopClient = new StubWorkshopClient(new Dictionary<string, WorkshopMetadata>
        {
            ["100"] = CreateMetadata("100", "First imported mod"),
            ["400"] = CreateMetadata("400", "Second imported mod"),
        });
        using var coordinator = new WriteOperationCoordinator();
        var service = new LibraryService(
            settings,
            repository,
            workshopClient,
            junction,
            coordinator,
            junctionPath);

        var result = await service.ScanAsync(root);

        Assert.Equal(OperationStatus.Succeeded, result.Status);
        Assert.Equal(2, result.ImportedCount);
        Assert.Equal(1, result.IgnoredDirectoryCount);
        Assert.Equal(["300"], result.EmptyWorkshopIds);
        Assert.Equal(["400"], result.AddedWorkshopIds);
        Assert.Equal(["200"], result.RemovedWorkshopIds);
        Assert.Equal(["100", "400"], result.Records.Select(record => record.WorkshopId));
        Assert.Equal(["First imported mod", "Second imported mod"], result.Records.Select(record => record.Title));
        Assert.Equal(1, workshopClient.CallCount);
        Assert.Equal(["100", "400"], workshopClient.LastRequestedIds);
        var existing = Assert.Single(result.Records, record => record.WorkshopId == "100");
        Assert.Equal(TestTime, existing.ImportedOrDownloadedAtUtc);
        var imported = Assert.Single(result.Records, record => record.WorkshopId == "400");
        Assert.Equal(new DateTimeOffset(newImportTimestamp), imported.ImportedOrDownloadedAtUtc);
        var persistedRecords = await repository.ListAsync(root);
        Assert.Equal(["100", "400"], persistedRecords.Select(record => record.WorkshopId));
        Assert.Equal(["First imported mod", "Second imported mod"], persistedRecords.Select(record => record.Title));
    }

    [Fact]
    public async Task RescanRefreshesMetadataWhilePreservingInstalledStateAndFallbackMetadata()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var root = temporaryDirectory.GetPath("library");
        await CreateNonEmptyDirectoryAsync(root, "100");
        await CreateNonEmptyDirectoryAsync(root, "200");
        await CreateNonEmptyDirectoryAsync(root, "300");
        var newImportTimestamp = new DateTime(2024, 2, 3, 4, 5, 6, DateTimeKind.Utc);
        Directory.SetLastWriteTimeUtc(Path.Combine(root, "300"), newImportTimestamp);

        var importedAtUtc = TestTime.AddDays(-10);
        var installedWorkshopUpdatedAtUtc = TestTime.AddDays(-5);
        var existingWithFreshMetadata = CreateRecord(root, "100") with
        {
            Title = "Cached title",
            Description = "Cached description",
            FileSize = 10,
            ImportedOrDownloadedAtUtc = importedAtUtc,
            InstalledWorkshopUpdatedAtUtc = installedWorkshopUpdatedAtUtc,
            LastOperationStatus = OperationStatus.Failed,
            LastError = "Previous update failed.",
        };
        var existingWithFailedMetadata = CreateRecord(root, "200") with
        {
            Title = "Fallback title",
            Description = "Fallback description",
            PreviewUrl = "https://example.test/old-preview.jpg",
            CreatorId = "old-creator",
            CreatedAtUtc = TestTime.AddYears(-1),
            FileSize = 20,
        };
        using var repository = new SqliteModRepository(temporaryDirectory.GetPath("library.db"));
        await repository.InitializeAsync();
        await repository.ReplaceSnapshotAsync(
            root,
            [existingWithFreshMetadata, existingWithFailedMetadata],
            TestTime);
        await repository.MarkCacheStaleAsync();
        var workshopClient = new StubWorkshopClient(new Dictionary<string, WorkshopMetadata>
        {
            ["100"] = new WorkshopMetadata
            {
                WorkshopId = "100",
                Title = "Remote title",
                Description = "Remote description",
                PreviewUrl = "https://example.test/new-preview.jpg",
                CreatorId = "new-creator",
                CreatedAtUtc = TestTime.AddYears(-2),
                FileSize = 30,
            },
        });
        var settings = new StubSettingsStore(new AppSettings { LibraryRoot = root });
        var junctionPath = temporaryDirectory.GetPath(Path.Combine("steamcmd", "281990"));
        var junction = new MemoryJunctionManager(junctionPath, root);
        using var coordinator = new WriteOperationCoordinator();
        var service = new LibraryService(
            settings,
            repository,
            workshopClient,
            junction,
            coordinator,
            junctionPath,
            new FixedTimeProvider(TestTime.AddHours(1)));

        var result = await service.ScanAsync(root);

        Assert.Equal(OperationStatus.Succeeded, result.Status);
        Assert.Equal(1, workshopClient.CallCount);
        Assert.Equal(["100", "200", "300"], workshopClient.LastRequestedIds);
        var refreshed = Assert.Single(result.Records, record => record.WorkshopId == "100");
        Assert.Equal("Remote title", refreshed.Title);
        Assert.Equal("Remote description", refreshed.Description);
        Assert.Equal("https://example.test/new-preview.jpg", refreshed.PreviewUrl);
        Assert.Equal("new-creator", refreshed.CreatorId);
        Assert.Equal(TestTime.AddYears(-2), refreshed.CreatedAtUtc);
        Assert.Equal(30, refreshed.FileSize);
        Assert.Equal(importedAtUtc, refreshed.ImportedOrDownloadedAtUtc);
        Assert.Equal(installedWorkshopUpdatedAtUtc, refreshed.InstalledWorkshopUpdatedAtUtc);
        Assert.Equal(OperationStatus.Failed, refreshed.LastOperationStatus);
        Assert.Equal("Previous update failed.", refreshed.LastError);

        var fallback = Assert.Single(result.Records, record => record.WorkshopId == "200");
        Assert.Equal("Fallback title", fallback.Title);
        Assert.Equal("Fallback description", fallback.Description);
        Assert.Equal("https://example.test/old-preview.jpg", fallback.PreviewUrl);
        Assert.Equal("old-creator", fallback.CreatorId);
        Assert.Equal(TestTime.AddYears(-1), fallback.CreatedAtUtc);
        Assert.Equal(20, fallback.FileSize);

        var imported = Assert.Single(result.Records, record => record.WorkshopId == "300");
        Assert.Null(imported.Title);
        Assert.Null(imported.Description);
        Assert.Equal(new DateTimeOffset(newImportTimestamp), imported.ImportedOrDownloadedAtUtc);
        Assert.Null(imported.InstalledWorkshopUpdatedAtUtc);
        Assert.Equal(OperationStatus.Succeeded, imported.LastOperationStatus);
        Assert.Null(imported.LastError);

        var persisted = await repository.ListAsync(root);
        Assert.Equal(result.Records, persisted);
    }

    [Fact]
    public async Task MetadataRequestFailureDoesNotBlockTheScanTransaction()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var root = temporaryDirectory.GetPath("library");
        await CreateNonEmptyDirectoryAsync(root, "100");
        using var repository = new SqliteModRepository(temporaryDirectory.GetPath("library.db"));
        await repository.InitializeAsync();
        var settings = new StubSettingsStore(new AppSettings { LibraryRoot = root });
        var junctionPath = temporaryDirectory.GetPath(Path.Combine("steamcmd", "281990"));
        var junction = new MemoryJunctionManager(junctionPath, root);
        using var coordinator = new WriteOperationCoordinator();
        var service = new LibraryService(
            settings,
            repository,
            new FailingWorkshopClient(),
            junction,
            coordinator,
            junctionPath);

        var result = await service.ScanAsync(root);

        Assert.Equal(OperationStatus.Succeeded, result.Status);
        var imported = Assert.Single(result.Records);
        Assert.Equal("100", imported.WorkshopId);
        Assert.Null(imported.Title);
        Assert.Equal(CacheState.Valid, (await repository.GetCacheStateAsync(root)).State);
        Assert.Single(await repository.ListAsync(root));
    }

    [Fact]
    public async Task SuccessfulSwitchCommitsSettingsThenRebuildsTheNewRoot()
    {
        var workshopClient = new StubWorkshopClient(new Dictionary<string, WorkshopMetadata>
        {
            ["200"] = CreateMetadata("200", "Imported from the switched library"),
        });
        using var fixture = await LibraryServiceFixture.CreateAsync(workshopClient: workshopClient);
        await CreateNonEmptyDirectoryAsync(fixture.NewRoot, "200");
        var proposed = fixture.Settings.Settings with
        {
            LibraryRoot = fixture.NewRoot,
            Language = AppSettings.SimplifiedChineseLanguage,
            CheckModUpdatesOnStartup = true,
        };

        var result = await fixture.Service.SwitchAsync(proposed);

        Assert.Equal(OperationStatus.Succeeded, result.Status);
        Assert.True(result.SettingsCommitted);
        Assert.Equal(fixture.NewRoot, fixture.Settings.Settings.LibraryRoot);
        Assert.Equal(AppSettings.SimplifiedChineseLanguage, fixture.Settings.Settings.Language);
        Assert.True(fixture.Settings.Settings.CheckModUpdatesOnStartup);
        Assert.Equal(fixture.NewRoot, fixture.Junction.State.TargetPath);
        Assert.Equal(["200"], result.ScanResult?.AddedWorkshopIds);
        Assert.Equal(["100"], result.ScanResult?.RemovedWorkshopIds);
        Assert.Equal("Imported from the switched library", result.ScanResult?.Records.Single().Title);
        Assert.Equal(["200"], workshopClient.LastRequestedIds);
        Assert.Equal(CacheState.Valid, (await fixture.Repository.GetCacheStateAsync(fixture.NewRoot)).State);
        Assert.Equal(
            "Imported from the switched library",
            (await fixture.Repository.ListAsync(fixture.NewRoot)).Single().Title);
    }

    [Fact]
    public async Task JunctionFailureDoesNotChangeSettingsOrCache()
    {
        using var fixture = await LibraryServiceFixture.CreateAsync();
        fixture.Junction.FailSet = true;
        var originalSettings = fixture.Settings.Settings;

        var result = await fixture.Service.SwitchAsync(originalSettings with { LibraryRoot = fixture.NewRoot });

        Assert.Equal(OperationStatus.Failed, result.Status);
        Assert.False(result.SettingsCommitted);
        Assert.Equal(originalSettings, fixture.Settings.Settings);
        Assert.Equal(fixture.OldRoot, fixture.Junction.State.TargetPath);
        Assert.Equal(CacheState.Valid, (await fixture.Repository.GetCacheStateAsync(fixture.OldRoot)).State);
        Assert.Single(await fixture.Repository.ListAsync(fixture.OldRoot));
    }

    [Fact]
    public async Task SettingsFailureRestoresThePreviousJunctionAndLeavesCacheUntouched()
    {
        using var fixture = await LibraryServiceFixture.CreateAsync();
        fixture.Settings.FailSave = true;
        var originalSettings = fixture.Settings.Settings;

        var result = await fixture.Service.SwitchAsync(originalSettings with { LibraryRoot = fixture.NewRoot });

        Assert.Equal(OperationStatus.Failed, result.Status);
        Assert.False(result.SettingsCommitted);
        Assert.False(result.RequiresManualRepair);
        Assert.Equal(1, fixture.Junction.RestoreCount);
        Assert.Equal(fixture.OldRoot, fixture.Junction.State.TargetPath);
        Assert.Equal(originalSettings, fixture.Settings.Settings);
        Assert.Equal(CacheState.Valid, (await fixture.Repository.GetCacheStateAsync(fixture.OldRoot)).State);
    }

    [Fact]
    public async Task CacheRebuildFailureKeepsNewSettingsAndJunctionAndAllowsRetry()
    {
        using var fixture = await LibraryServiceFixture.CreateAsync(failReplacement: true);
        await CreateNonEmptyDirectoryAsync(fixture.NewRoot, "200");

        var result = await fixture.Service.SwitchAsync(
            fixture.Settings.Settings with { LibraryRoot = fixture.NewRoot });

        Assert.Equal(OperationStatus.Failed, result.Status);
        Assert.True(result.SettingsCommitted);
        Assert.True(result.CanRetryScan);
        Assert.Equal(fixture.NewRoot, fixture.Settings.Settings.LibraryRoot);
        Assert.Equal(fixture.NewRoot, fixture.Junction.State.TargetPath);
        Assert.Equal(CacheState.Stale, (await fixture.Repository.GetCacheStateAsync(fixture.OldRoot)).State);
        Assert.Equal(CacheState.RootMismatch, (await fixture.Repository.GetCacheStateAsync(fixture.NewRoot)).State);
        Assert.Empty(await fixture.Repository.ListAsync(fixture.NewRoot));
    }

    [Fact]
    public async Task EnsureJunctionRepairsMismatchUsingTheConfiguredTarget()
    {
        using var fixture = await LibraryServiceFixture.CreateAsync();
        fixture.Junction.SetExternalTarget(fixture.NewRoot);

        var result = await fixture.Service.EnsureJunctionAsync(fixture.OldRoot);

        Assert.Equal(OperationStatus.Succeeded, result.Status);
        Assert.True(result.Changed);
        Assert.Equal(fixture.OldRoot, fixture.Junction.State.TargetPath);
    }

    [Fact]
    public async Task ValidationRejectsFilesAndPathsThatOverlapTheJunction()
    {
        using var fixture = await LibraryServiceFixture.CreateAsync();
        var filePath = fixture.TemporaryDirectory.GetPath("library-file");
        await File.WriteAllTextAsync(filePath, "not a directory");

        Assert.False((await fixture.Service.ValidateAsync(filePath)).IsValid);
        Assert.False((await fixture.Service.ValidateAsync(fixture.JunctionPath)).IsValid);
        Assert.False((await fixture.Service.ValidateAsync(Path.GetDirectoryName(fixture.JunctionPath))).IsValid);
    }

    private static async Task CreateNonEmptyDirectoryAsync(string parent, string name)
    {
        var path = Path.Combine(parent, name);
        Directory.CreateDirectory(path);
        await File.WriteAllTextAsync(Path.Combine(path, "content.txt"), "content");
    }

    private static ModRecord CreateRecord(string root, string workshopId)
    {
        return new ModRecord
        {
            WorkshopId = workshopId,
            ContentPath = Path.Combine(root, workshopId),
            ImportedOrDownloadedAtUtc = TestTime,
            LastScannedAtUtc = TestTime,
        };
    }

    private static WorkshopMetadata CreateMetadata(string workshopId, string title)
    {
        return new WorkshopMetadata
        {
            WorkshopId = workshopId,
            Title = title,
        };
    }

    private sealed class FailingWorkshopClient : IWorkshopClient
    {
        public Task<IReadOnlyDictionary<string, WorkshopMetadata>> GetMetadataBatchAsync(
            IReadOnlyCollection<string> workshopIds,
            IProgress<OperationProgress>? progress = null,
            CancellationToken cancellationToken = default) =>
            throw new HttpRequestException("Forced Workshop metadata failure.");
    }

    private sealed class LibraryServiceFixture : IDisposable
    {
        private readonly WriteOperationCoordinator coordinator;

        private LibraryServiceFixture(
            TemporaryDirectory temporaryDirectory,
            SqliteModRepository repository,
            StubSettingsStore settings,
            MemoryJunctionManager junction,
            WriteOperationCoordinator coordinator,
            LibraryService service,
            string oldRoot,
            string newRoot,
            string junctionPath)
        {
            TemporaryDirectory = temporaryDirectory;
            Repository = repository;
            Settings = settings;
            Junction = junction;
            this.coordinator = coordinator;
            Service = service;
            OldRoot = oldRoot;
            NewRoot = newRoot;
            JunctionPath = junctionPath;
        }

        public TemporaryDirectory TemporaryDirectory { get; }

        public SqliteModRepository Repository { get; }

        public StubSettingsStore Settings { get; }

        public MemoryJunctionManager Junction { get; }

        public LibraryService Service { get; }

        public string OldRoot { get; }

        public string NewRoot { get; }

        public string JunctionPath { get; }

        public static async Task<LibraryServiceFixture> CreateAsync(
            bool failReplacement = false,
            IWorkshopClient? workshopClient = null)
        {
            var temporaryDirectory = new TemporaryDirectory();
            var oldRoot = temporaryDirectory.GetPath("old-library");
            var newRoot = temporaryDirectory.GetPath("new-library");
            Directory.CreateDirectory(oldRoot);
            Directory.CreateDirectory(newRoot);
            await CreateNonEmptyDirectoryAsync(oldRoot, "100");
            var repository = new SqliteModRepository(temporaryDirectory.GetPath("library.db"));
            await repository.InitializeAsync();
            await repository.ReplaceSnapshotAsync(oldRoot, [CreateRecord(oldRoot, "100")], TestTime);
            var settings = new StubSettingsStore(new AppSettings { LibraryRoot = oldRoot });
            var junctionPath = temporaryDirectory.GetPath(
                Path.Combine("steamcmd", "steamapps", "workshop", "content", "281990"));
            var junction = new MemoryJunctionManager(junctionPath, oldRoot);
            var coordinator = new WriteOperationCoordinator();
            IModRepository serviceRepository = failReplacement
                ? new FailingReplaceModRepository(repository)
                : repository;
            var service = new LibraryService(
                settings,
                serviceRepository,
                workshopClient ?? new StubWorkshopClient(),
                junction,
                coordinator,
                junctionPath);
            return new LibraryServiceFixture(
                temporaryDirectory,
                repository,
                settings,
                junction,
                coordinator,
                service,
                oldRoot,
                newRoot,
                junctionPath);
        }

        public void Dispose()
        {
            coordinator.Dispose();
            Repository.Dispose();
            TemporaryDirectory.Dispose();
            GC.SuppressFinalize(this);
        }
    }
}
