using StellarisDownloader.App.Services;
using StellarisDownloader.Core.Integrations;
using StellarisDownloader.Core.Models;
using StellarisDownloader.Core.Services;

namespace StellarisDownloader.Tests;

public sealed class InstalledWorkshopStateProviderTests
{
    private static readonly DateTimeOffset TestTime = new(
        2026,
        1,
        2,
        3,
        4,
        5,
        TimeSpan.Zero);

    [Fact]
    public async Task UninitializedOrEmptySettingsReturnNoInstalledIdsWithoutReadingTheCache()
    {
        var repository = new StubModRepository(CacheState.Valid, []);
        var uninitializedProvider = new InstalledWorkshopStateProvider(
            new FixedSettingsStore(
                new AppSettings { LibraryRoot = "C:\\configured" },
                requiresInitialization: true),
            repository);
        var emptyProvider = new InstalledWorkshopStateProvider(
            new FixedSettingsStore(
                new AppSettings { LibraryRoot = string.Empty },
                requiresInitialization: false),
            repository);

        Assert.Empty(await uninitializedProvider.GetInstalledWorkshopIdsAsync());
        Assert.Empty(await emptyProvider.GetInstalledWorkshopIdsAsync());
        Assert.Equal(0, repository.GetCacheStateCallCount);
        Assert.Equal(0, repository.ListCallCount);
    }

    [Theory]
    [InlineData(CacheState.Stale)]
    [InlineData(CacheState.RootMismatch)]
    public async Task UnsafeCacheStateReturnsNoInstalledIdsWithoutListingRecords(
        CacheState cacheState)
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var libraryRoot = temporaryDirectory.GetPath("library");
        Directory.CreateDirectory(libraryRoot);
        var repository = new StubModRepository(cacheState, []);
        var provider = CreateProvider(libraryRoot, repository);

        Assert.Empty(await provider.GetInstalledWorkshopIdsAsync());
        Assert.Equal(1, repository.GetCacheStateCallCount);
        Assert.Equal(0, repository.ListCallCount);
    }

    [Fact]
    public async Task OnlyRealNonEmptyDirectNumericDirectoriesAreReturnedInFirstSeenOrder()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var libraryRoot = temporaryDirectory.GetPath("library");
        var outsideRoot = temporaryDirectory.GetPath("outside");
        Directory.CreateDirectory(libraryRoot);
        Directory.CreateDirectory(outsideRoot);
        CreateNonEmptyDirectory(libraryRoot, "200");
        CreateNonEmptyDirectory(libraryRoot, "100");
        CreateNonEmptyDirectory(libraryRoot, "600");
        CreateNonEmptyDirectory(libraryRoot, "800");
        CreateNonEmptyDirectory(outsideRoot, "500");
        Directory.CreateDirectory(Path.Combine(libraryRoot, "400"));
        File.WriteAllText(Path.Combine(libraryRoot, "900"), "not a directory");

        var records = new[]
        {
            CreateRecord(libraryRoot, "200", lastStatus: OperationStatus.Failed),
            CreateRecord(libraryRoot, "300"),
            CreateRecord(libraryRoot, "200"),
            CreateRecord(libraryRoot, "400"),
            CreateRecord(libraryRoot, "600", Path.Combine(outsideRoot, "500")),
            CreateRecord(libraryRoot, "700", libraryRoot),
            CreateRecord(libraryRoot, "800", "\0"),
            CreateRecord(libraryRoot, "100"),
            CreateRecord(libraryRoot, "900"),
            CreateRecord(libraryRoot, "\uFF11\uFF12\uFF13"),
            CreateRecord(libraryRoot, "../outside"),
        };
        var repository = new StubModRepository(CacheState.Valid, records);
        var provider = CreateProvider(libraryRoot, repository);

        var installedIds = await provider.GetInstalledWorkshopIdsAsync();

        Assert.Equal(["200", "100"], installedIds);
        Assert.Equal(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(libraryRoot)),
            repository.LastExpectedLibraryRoot,
            ignoreCase: true);
        Assert.Equal(1, repository.ListCallCount);
    }

    [Fact]
    public async Task ReparsePointDirectoryIsNotReportedAsInstalled()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var libraryRoot = temporaryDirectory.GetPath("library");
        var externalTarget = temporaryDirectory.GetPath("external-target");
        Directory.CreateDirectory(libraryRoot);
        Directory.CreateDirectory(externalTarget);
        await File.WriteAllTextAsync(Path.Combine(externalTarget, "sentinel.txt"), "keep");
        var junctionPath = Path.Combine(libraryRoot, "123");
        var junctionManager = new WindowsJunctionManager();
        var update = await junctionManager.SetTargetAsync(junctionPath, externalTarget);

        try
        {
            var repository = new StubModRepository(
                CacheState.Valid,
                [CreateRecord(libraryRoot, "123")]);
            var provider = CreateProvider(libraryRoot, repository);

            Assert.Empty(await provider.GetInstalledWorkshopIdsAsync());
            Assert.True(File.Exists(Path.Combine(externalTarget, "sentinel.txt")));
        }
        finally
        {
            await junctionManager.RestoreAsync(update, CancellationToken.None);
        }
    }

    [Fact]
    public async Task RepositoryCancellationIsPropagated()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var libraryRoot = temporaryDirectory.GetPath("library");
        Directory.CreateDirectory(libraryRoot);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var repository = new StubModRepository(CacheState.Valid, [])
        {
            GetCacheStateCancellation = cancellation.Token,
        };
        var provider = CreateProvider(libraryRoot, repository);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => provider.GetInstalledWorkshopIdsAsync(CancellationToken.None));
    }

    [Fact]
    public async Task SettingsCancellationIsPropagatedBeforeTheCacheIsRead()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var repository = new StubModRepository(CacheState.Valid, []);
        var provider = new InstalledWorkshopStateProvider(
            new FixedSettingsStore(
                new AppSettings { LibraryRoot = "C:\\configured" },
                requiresInitialization: false),
            repository);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => provider.GetInstalledWorkshopIdsAsync(cancellation.Token));
        Assert.Equal(0, repository.GetCacheStateCallCount);
    }

    private static InstalledWorkshopStateProvider CreateProvider(
        string libraryRoot,
        IModRepository repository) =>
        new(
            new FixedSettingsStore(
                new AppSettings { LibraryRoot = libraryRoot },
                requiresInitialization: false),
            repository);

    private static void CreateNonEmptyDirectory(string parent, string name)
    {
        var path = Path.Combine(parent, name);
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "content.bin"), "content");
    }

    private static ModRecord CreateRecord(
        string libraryRoot,
        string workshopId,
        string? contentPath = null,
        OperationStatus lastStatus = OperationStatus.Succeeded) =>
        new()
        {
            WorkshopId = workshopId,
            ContentPath = contentPath ?? Path.Combine(libraryRoot, workshopId),
            ImportedOrDownloadedAtUtc = TestTime,
            LastScannedAtUtc = TestTime,
            LastOperationStatus = lastStatus,
        };

    private sealed class FixedSettingsStore : ISettingsStore
    {
        private readonly SettingsLoadResult loadResult;

        public FixedSettingsStore(AppSettings settings, bool requiresInitialization)
        {
            loadResult = new SettingsLoadResult(
                settings,
                requiresInitialization,
                CorruptBackupPath: null);
        }

        public Task<SettingsLoadResult> LoadAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(loadResult);
        }

        public Task SaveAsync(
            AppSettings settings,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class StubModRepository : IModRepository
    {
        private readonly CacheState cacheState;
        private readonly IReadOnlyList<ModRecord> records;

        public StubModRepository(CacheState cacheState, IReadOnlyList<ModRecord> records)
        {
            this.cacheState = cacheState;
            this.records = records;
        }

        public int GetCacheStateCallCount { get; private set; }

        public int ListCallCount { get; private set; }

        public string? LastExpectedLibraryRoot { get; private set; }

        public CancellationToken GetCacheStateCancellation { get; init; }

        public Task InitializeAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<ModRecord>> ListAsync(
            string libraryRoot,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ListCallCount++;
            LastExpectedLibraryRoot = libraryRoot;
            return Task.FromResult(records);
        }

        public Task<ModRecord?> GetAsync(
            string libraryRoot,
            string workshopId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task UpsertFinalResultAsync(
            string libraryRoot,
            ModRecord record,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> DeleteAsync(
            string libraryRoot,
            string workshopId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task ReplaceSnapshotAsync(
            string libraryRoot,
            IReadOnlyCollection<ModRecord> records,
            DateTimeOffset rebuiltAtUtc,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<CacheStateInfo> GetCacheStateAsync(
            string? expectedLibraryRoot,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            GetCacheStateCallCount++;
            LastExpectedLibraryRoot = expectedLibraryRoot;
            if (GetCacheStateCancellation.IsCancellationRequested)
            {
                return Task.FromCanceled<CacheStateInfo>(GetCacheStateCancellation);
            }

            return Task.FromResult(new CacheStateInfo(
                SchemaVersion: 1,
                LibraryRoot: expectedLibraryRoot,
                cacheState,
                LastRebuiltAtUtc: TestTime));
        }

        public Task MarkCacheStaleAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
