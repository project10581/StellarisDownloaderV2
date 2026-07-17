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
        using var coordinator = new WriteOperationCoordinator();
        var service = new LibraryService(
            settings,
            repository,
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
        var imported = Assert.Single(result.Records, record => record.WorkshopId == "100");
        Assert.Equal(new DateTimeOffset(oldTimestamp), imported.ImportedOrDownloadedAtUtc);
        Assert.Equal(["100", "400"], (await repository.ListAsync(root)).Select(record => record.WorkshopId));
    }

    [Fact]
    public async Task SuccessfulSwitchCommitsSettingsThenRebuildsTheNewRoot()
    {
        using var fixture = await LibraryServiceFixture.CreateAsync();
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
        Assert.Equal(CacheState.Valid, (await fixture.Repository.GetCacheStateAsync(fixture.NewRoot)).State);
        Assert.Single(await fixture.Repository.ListAsync(fixture.NewRoot));
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

        public static async Task<LibraryServiceFixture> CreateAsync(bool failReplacement = false)
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
