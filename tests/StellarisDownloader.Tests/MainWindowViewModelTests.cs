using System.Windows.Media;
using StellarisDownloader.App.ViewModels;
using StellarisDownloader.Core.Models;
using StellarisDownloader.Core.Persistence;
using StellarisDownloader.Core.Services;

namespace StellarisDownloader.Tests;

public sealed class MainWindowViewModelTests
{
    private static readonly DateTimeOffset TestTime = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

    [Fact]
    public async Task SearchMatchesTitleAndWorkshopIdWhileListItemsDisplayOnlyTitles()
    {
        using var fixture = await MainWindowFixture.CreateAsync(CreateRecords());
        await fixture.ViewModel.InitializeAsync();
        AssertReady(fixture.ViewModel);

        fixture.ViewModel.SearchText = "200";
        var result = Assert.Single(VisibleItems(fixture.ViewModel));

        Assert.Equal("Bravo", result.DisplayTitle);
        Assert.DoesNotContain(result.WorkshopId, result.DisplayTitle, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FourSortOptionsProduceTheExpectedOrders()
    {
        using var fixture = await MainWindowFixture.CreateAsync(CreateRecords());
        await fixture.ViewModel.InitializeAsync();
        AssertReady(fixture.ViewModel);

        AssertOrder(fixture.ViewModel, ModSortOption.Title, "100", "200", "300");
        AssertOrder(fixture.ViewModel, ModSortOption.RemoteUpdated, "200", "300", "100");
        AssertOrder(fixture.ViewModel, ModSortOption.LastDownloaded, "100", "300", "200");
        AssertOrder(fixture.ViewModel, ModSortOption.FileSize, "200", "300", "100");
    }

    [Fact]
    public async Task SelectionChangesOnlyTheSelectedDetailRecord()
    {
        using var fixture = await MainWindowFixture.CreateAsync(CreateRecords());
        await fixture.ViewModel.InitializeAsync();
        AssertReady(fixture.ViewModel);
        var selected = VisibleItems(fixture.ViewModel)[1];

        fixture.ViewModel.SelectedMod = selected;

        Assert.True(fixture.ViewModel.HasSelection);
        Assert.Same(selected, fixture.ViewModel.SelectedMod);
        Assert.Equal("200", fixture.ViewModel.SelectedMod.Record.WorkshopId);
        Assert.Equal(3, VisibleItems(fixture.ViewModel).Count);
    }

    [Fact]
    public async Task StaleCacheClearsRowsAndDisablesLibraryWrites()
    {
        using var fixture = await MainWindowFixture.CreateAsync(CreateRecords());
        await fixture.ViewModel.InitializeAsync();
        await fixture.Repository.MarkCacheStaleAsync();

        await fixture.ViewModel.RefreshAsync();

        Assert.Empty(VisibleItems(fixture.ViewModel));
        Assert.Equal(LibraryViewState.Stale, fixture.ViewModel.State);
        Assert.False(fixture.ViewModel.CanModifyLibrary);
        Assert.False(fixture.ViewModel.CanRunWriteOperations);
        Assert.True(fixture.ViewModel.RescanCommand.CanExecute(null));
    }

    [Fact]
    public async Task JunctionRepairFailurePreventsRowsAndDangerousOperations()
    {
        using var fixture = await MainWindowFixture.CreateAsync(CreateRecords());
        fixture.Library.JunctionResult = new JunctionEnsureResult(
            OperationStatus.Failed,
            "junction",
            fixture.LibraryRoot,
            Changed: false,
            "Junction repair failed.");

        await fixture.ViewModel.RefreshAsync();

        Assert.Equal(LibraryViewState.Error, fixture.ViewModel.State);
        Assert.Equal("Junction repair failed.", fixture.ViewModel.LastError);
        Assert.Empty(VisibleItems(fixture.ViewModel));
        Assert.False(fixture.ViewModel.CanRunWriteOperations);
        Assert.True(fixture.ViewModel.RescanCommand.CanExecute(null));
    }

    [Fact]
    public async Task StartupRefreshEnsuresJunctionBeforeScanning()
    {
        using var fixture = await MainWindowFixture.CreateAsync(
            CreateRecords(),
            refreshOnStartup: true);

        await fixture.ViewModel.InitializeAsync();

        Assert.Equal(2, fixture.Library.EnsureJunctionCount);
        Assert.Equal(1, fixture.Library.ScanCount);
        Assert.Equal(LibraryViewState.Ready, fixture.ViewModel.State);
    }

    [Fact]
    public void RunningWriteOperationDisablesConflictingCommandsUntilCompletion()
    {
        WpfTestRunner.Run(async () =>
        {
            using var fixture = await MainWindowFixture.CreateAsync(CreateRecords());
            await fixture.ViewModel.InitializeAsync();
            AssertReady(fixture.ViewModel);
            var completion = new TaskCompletionSource<LibraryScanResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            fixture.Library.PendingScan = completion;

            var scanTask = fixture.ViewModel.RescanAsync();

            Assert.True(fixture.ViewModel.IsBusy);
            Assert.False(fixture.ViewModel.CanRunWriteOperations);
            Assert.False(fixture.ViewModel.RescanCommand.CanExecute(null));

            completion.SetResult(new LibraryScanResult
            {
                Status = OperationStatus.Succeeded,
                LibraryRoot = fixture.LibraryRoot,
                Records = CreateRecords(),
            });
            await scanTask;

            Assert.False(fixture.ViewModel.IsBusy);
            Assert.True(fixture.ViewModel.CanRunWriteOperations);
        });
    }

    [Fact]
    public async Task ANewSelectionPreventsAnOlderPreviewRequestFromOverwritingIt()
    {
        var preview = new StubPreviewImageService();
        var firstResponse = preview.EnqueueResponse();
        var secondResponse = preview.EnqueueResponse();
        using var fixture = await MainWindowFixture.CreateAsync(CreateRecords(withPreviews: true), preview);
        await fixture.ViewModel.InitializeAsync();
        AssertReady(fixture.ViewModel);
        var items = VisibleItems(fixture.ViewModel);
        var firstImage = new DrawingImage();
        firstImage.Freeze();
        var secondImage = new DrawingImage();
        secondImage.Freeze();

        fixture.ViewModel.SelectedMod = items[0];
        fixture.ViewModel.SelectedMod = items[1];
        secondResponse.SetResult(secondImage);
        await WaitUntilAsync(() => ReferenceEquals(fixture.ViewModel.PreviewImage, secondImage));
        firstResponse.SetResult(firstImage);
        await Task.Delay(50);

        Assert.Same(secondImage, fixture.ViewModel.PreviewImage);
        Assert.Equal(2, preview.CallCount);
    }

    [Fact]
    public async Task DeleteIsRejectedWithoutASelection()
    {
        using var fixture = await MainWindowFixture.CreateAsync(CreateRecords());
        await fixture.ViewModel.InitializeAsync();
        AssertReady(fixture.ViewModel);

        var result = await fixture.ViewModel.DeleteSelectedAsync(permanently: false);

        Assert.False(result.Started);
        Assert.Null(result.DeleteResult);
        Assert.Equal(0, fixture.Operations.DeleteCallCount);
    }

    [Fact]
    public async Task DeleteIsRejectedWhenTheCacheIsUnsafe()
    {
        using var fixture = await MainWindowFixture.CreateAsync(CreateRecords());
        await fixture.ViewModel.InitializeAsync();
        fixture.ViewModel.SelectedMod = VisibleItems(fixture.ViewModel)[0];
        await fixture.Repository.MarkCacheStaleAsync();
        await fixture.ViewModel.RefreshAsync();

        var result = await fixture.ViewModel.DeleteSelectedAsync(permanently: false);

        Assert.False(result.Started);
        Assert.Equal(LibraryViewState.Stale, fixture.ViewModel.State);
        Assert.Equal(0, fixture.Operations.DeleteCallCount);
    }

    [Fact]
    public async Task SuccessfulRecycleDeleteRefreshesTheLibrary()
    {
        using var fixture = await MainWindowFixture.CreateAsync(CreateRecords());
        await fixture.ViewModel.InitializeAsync();
        fixture.ViewModel.SelectedMod = VisibleItems(fixture.ViewModel)[0];
        fixture.Operations.DeleteHandler = async (root, id, permanently, _, token) =>
        {
            Assert.False(permanently);
            Assert.True(await fixture.Repository.DeleteAsync(root, id, token));
            return DeleteResultFor(id, OperationStatus.Succeeded, recordRemoved: true);
        };

        var result = await fixture.ViewModel.DeleteSelectedAsync(permanently: false);

        Assert.True(result.Started);
        Assert.Equal(OperationStatus.Succeeded, result.DeleteResult?.Status);
        Assert.Equal(2, VisibleItems(fixture.ViewModel).Count);
        Assert.Null(fixture.ViewModel.SelectedMod);
        Assert.Equal(2, fixture.Library.EnsureJunctionCount);
    }

    [Fact]
    public async Task RecycleFailureReturnsThePermanentRetrySignalWithoutFallingBack()
    {
        using var fixture = await MainWindowFixture.CreateAsync(CreateRecords());
        await fixture.ViewModel.InitializeAsync();
        fixture.ViewModel.SelectedMod = VisibleItems(fixture.ViewModel)[0];
        fixture.Operations.DeleteHandler = (_, id, permanently, _, _) =>
        {
            Assert.False(permanently);
            return Task.FromResult(DeleteResultFor(
                id,
                OperationStatus.Failed,
                canRetryPermanently: true,
                error: "Recycle Bin failed."));
        };

        var result = await fixture.ViewModel.DeleteSelectedAsync(permanently: false);

        Assert.True(result.CanRetryPermanently);
        Assert.False(result.QueuedForRedownload);
        Assert.Equal("Recycle Bin failed.", fixture.ViewModel.LastError);
        Assert.Equal(1, fixture.Operations.DeleteCallCount);
        Assert.Equal(0, fixture.Operations.PermanentDeleteCallCount);
    }

    [Fact]
    public async Task CacheFailureAfterFileRemovalMakesTheLibraryStaleAndRequiresRescan()
    {
        using var fixture = await MainWindowFixture.CreateAsync(CreateRecords());
        await fixture.ViewModel.InitializeAsync();
        fixture.ViewModel.SelectedMod = VisibleItems(fixture.ViewModel)[0];
        fixture.Operations.DeleteHandler = (_, id, _, _, _) => Task.FromResult(DeleteResultFor(
            id,
            OperationStatus.Failed,
            filesRemoved: true,
            requiresRescan: true,
            error: "Cache delete failed."));

        var result = await fixture.ViewModel.DeleteSelectedAsync(permanently: false);

        Assert.True(result.RequiresRescan);
        Assert.Equal(LibraryViewState.Stale, fixture.ViewModel.State);
        Assert.False(fixture.ViewModel.CanModifyLibrary);
        Assert.Empty(VisibleItems(fixture.ViewModel));
        Assert.Contains("Rescan", fixture.ViewModel.LastError, StringComparison.Ordinal);
        Assert.Equal(
            CacheState.Stale,
            (await fixture.Repository.GetCacheStateAsync(fixture.LibraryRoot)).State);
    }

    [Fact]
    public async Task DeleteAndRedownloadUsesOnlyTheSharedQueue()
    {
        using var fixture = await MainWindowFixture.CreateAsync(CreateRecords());
        await fixture.ViewModel.InitializeAsync();
        fixture.ViewModel.SelectedMod = VisibleItems(fixture.ViewModel)[0];
        fixture.Operations.DeleteHandler = async (root, id, _, _, token) =>
        {
            Assert.True(await fixture.Repository.DeleteAsync(root, id, token));
            return DeleteResultFor(id, OperationStatus.Succeeded, recordRemoved: true);
        };

        var result = await fixture.ViewModel.DeleteAndQueueRedownloadAsync(permanently: false);

        Assert.True(result.QueuedForRedownload);
        Assert.Equal("100", Assert.Single(fixture.DownloadQueue.Items).WorkshopId);
        Assert.Equal(DownloadQueueItemStatus.Ready, fixture.DownloadQueue.Items[0].Status);
        Assert.Equal(0, fixture.Operations.RedownloadCallCount);
        Assert.Equal(1, fixture.Operations.DeleteCallCount);
    }

    [Fact]
    public void DeleteAndRedownloadReplacesATerminalDuplicateWithAReadyQueueItem()
    {
        WpfTestRunner.Run(async () =>
        {
            using var fixture = await MainWindowFixture.CreateAsync(CreateRecords());
            await fixture.ViewModel.InitializeAsync();
            await fixture.DownloadQueue.EnqueueAsync("100");
            await fixture.DownloadQueue.StartAsync();
            Assert.Equal(
                DownloadQueueItemStatus.Succeeded,
                Assert.Single(fixture.DownloadQueue.Items).Status);
            fixture.ViewModel.SelectedMod = VisibleItems(fixture.ViewModel)[0];
            fixture.Operations.DeleteHandler = async (root, id, _, _, token) =>
            {
                Assert.True(await fixture.Repository.DeleteAsync(root, id, token));
                return DeleteResultFor(id, OperationStatus.Succeeded, recordRemoved: true);
            };

            var result = await fixture.ViewModel.DeleteAndQueueRedownloadAsync(permanently: false);

            Assert.True(result.QueuedForRedownload);
            var replacement = Assert.Single(fixture.DownloadQueue.Items);
            Assert.Equal("100", replacement.WorkshopId);
            Assert.Equal(DownloadQueueItemStatus.Ready, replacement.Status);
            Assert.True(fixture.DownloadQueue.CanStart);
            Assert.Equal(0, fixture.Operations.RedownloadCallCount);
        });
    }

    [Fact]
    public async Task FailedDeleteDoesNotEnterTheDownloadQueue()
    {
        using var fixture = await MainWindowFixture.CreateAsync(CreateRecords());
        await fixture.ViewModel.InitializeAsync();
        fixture.ViewModel.SelectedMod = VisibleItems(fixture.ViewModel)[0];
        fixture.Operations.DeleteHandler = (_, id, _, _, _) => Task.FromResult(DeleteResultFor(
            id,
            OperationStatus.Failed,
            error: "Delete failed."));

        var result = await fixture.ViewModel.DeleteAndQueueRedownloadAsync(permanently: false);

        Assert.False(result.QueuedForRedownload);
        Assert.Empty(fixture.DownloadQueue.Items);
        Assert.Equal(0, fixture.Operations.RedownloadCallCount);
    }

    [Fact]
    public void RunningSharedQueueDisablesAndRejectsLibraryDelete()
    {
        WpfTestRunner.Run(async () =>
        {
            using var fixture = await MainWindowFixture.CreateAsync(CreateRecords());
            await fixture.ViewModel.InitializeAsync();
            fixture.ViewModel.SelectedMod = VisibleItems(fixture.ViewModel)[0];
            var entered = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource<DownloadBatchResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            fixture.Operations.DownloadHandler = (_, _, _) =>
            {
                entered.SetResult(true);
                return release.Task;
            };
            await fixture.DownloadQueue.EnqueueAsync("900");

            var queueRun = fixture.DownloadQueue.StartAsync();
            await entered.Task;

            Assert.False(fixture.ViewModel.CanRunWriteOperations);
            Assert.False(fixture.ViewModel.CanModifySelectedMod);
            Assert.False(fixture.ViewModel.RescanCommand.CanExecute(null));
            var deleteResult = await fixture.ViewModel.DeleteSelectedAsync(permanently: false);
            Assert.False(deleteResult.Started);
            Assert.Equal(0, fixture.Operations.DeleteCallCount);

            release.SetResult(new DownloadBatchResult
            {
                Results =
                [
                    new DownloadResult
                    {
                        WorkshopId = "900",
                        Status = OperationStatus.Succeeded,
                        ContentPath = "C:\\Mods\\900",
                    },
                ],
            });
            await queueRun;
            Assert.True(fixture.ViewModel.CanModifySelectedMod);
        });
    }

    private static IReadOnlyList<ModRecord> CreateRecords(bool withPreviews = false) =>
    [
        CreateRecord("100", "Alpha", 100, remoteOffset: 1, downloadOffset: 3, withPreviews),
        CreateRecord("200", "Bravo", 300, remoteOffset: 3, downloadOffset: 1, withPreviews),
        CreateRecord("300", "Charlie", 200, remoteOffset: 2, downloadOffset: 2, withPreviews),
    ];

    private static ModRecord CreateRecord(
        string id,
        string title,
        long size,
        int remoteOffset,
        int downloadOffset,
        bool withPreview) => new()
        {
            WorkshopId = id,
            Title = title,
            PreviewUrl = withPreview ? $"https://example.test/{id}.png" : null,
            ContentPath = Path.Combine("C:\\Mods", id),
            FileSize = size,
            ImportedOrDownloadedAtUtc = TestTime.AddHours(downloadOffset),
            InstalledWorkshopUpdatedAtUtc = TestTime.AddHours(remoteOffset),
            LastScannedAtUtc = TestTime,
        };

    private static List<ModListItemViewModel> VisibleItems(MainWindowViewModel viewModel) =>
        viewModel.ModsView.Cast<ModListItemViewModel>().ToList();

    private static void AssertOrder(
        MainWindowViewModel viewModel,
        ModSortOption sort,
        params string[] expectedIds)
    {
        viewModel.SelectedSort = sort;
        Assert.Equal(expectedIds, VisibleItems(viewModel).Select(item => item.WorkshopId));
    }

    private static void AssertReady(MainWindowViewModel viewModel) =>
        Assert.True(
            viewModel.State == LibraryViewState.Ready,
            $"Expected a ready library, but state was {viewModel.State}: {viewModel.LastError}");

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var timeout = DateTime.UtcNow.AddSeconds(2);
        while (!condition())
        {
            if (DateTime.UtcNow >= timeout)
            {
                throw new TimeoutException("The expected view-model state was not reached.");
            }

            await Task.Delay(10);
        }
    }

    private static DeleteResult DeleteResultFor(
        string workshopId,
        OperationStatus status,
        bool filesRemoved = false,
        bool recordRemoved = false,
        bool canRetryPermanently = false,
        bool requiresRescan = false,
        string? error = null) => new()
        {
            WorkshopId = workshopId,
            Status = status,
            ContentPath = Path.Combine("C:\\Mods", workshopId),
            FilesRemoved = filesRemoved,
            RecordRemoved = recordRemoved,
            CanRetryPermanently = canRetryPermanently,
            RequiresRescan = requiresRescan,
            Error = error,
        };

    private sealed class MainWindowFixture : IDisposable
    {
        private readonly TemporaryDirectory temporaryDirectory;

        private MainWindowFixture(
            TemporaryDirectory temporaryDirectory,
            SqliteModRepository repository,
            StubLibraryService library,
            MainWindowModOperationService operations,
            DownloadQueueViewModel downloadQueue,
            MainWindowViewModel viewModel,
            string libraryRoot)
        {
            this.temporaryDirectory = temporaryDirectory;
            Repository = repository;
            Library = library;
            Operations = operations;
            DownloadQueue = downloadQueue;
            ViewModel = viewModel;
            LibraryRoot = libraryRoot;
        }

        public SqliteModRepository Repository { get; }

        public StubLibraryService Library { get; }

        public MainWindowModOperationService Operations { get; }

        public DownloadQueueViewModel DownloadQueue { get; }

        public MainWindowViewModel ViewModel { get; }

        public string LibraryRoot { get; }

        public static async Task<MainWindowFixture> CreateAsync(
            IReadOnlyCollection<ModRecord> records,
            StubPreviewImageService? preview = null,
            bool refreshOnStartup = false)
        {
            var temporaryDirectory = new TemporaryDirectory();
            var root = temporaryDirectory.GetPath("library");
            Directory.CreateDirectory(root);
            var repository = new SqliteModRepository(temporaryDirectory.GetPath("library.db"));
            await repository.InitializeAsync();
            await repository.ReplaceSnapshotAsync(root, records, TestTime);
            var settings = new StubSettingsStore(new AppSettings
            {
                LibraryRoot = root,
                RefreshLibraryOnStartup = refreshOnStartup,
            });
            var library = new StubLibraryService();
            var operations = new MainWindowModOperationService();
            var downloadQueue = new DownloadQueueViewModel(
                settings,
                new MainWindowWorkshopClient(),
                operations);
            var viewModel = new MainWindowViewModel(
                settings,
                repository,
                library,
                preview ?? new StubPreviewImageService(),
                operations,
                downloadQueue);
            return new MainWindowFixture(
                temporaryDirectory,
                repository,
                library,
                operations,
                downloadQueue,
                viewModel,
                root);
        }

        public void Dispose()
        {
            ViewModel.Dispose();
            DownloadQueue.Dispose();
            Repository.Dispose();
            temporaryDirectory.Dispose();
        }
    }

    private sealed class MainWindowWorkshopClient : IWorkshopClient
    {
        public Task<IReadOnlyDictionary<string, WorkshopMetadata>> GetMetadataBatchAsync(
            IReadOnlyCollection<string> workshopIds,
            IProgress<OperationProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyDictionary<string, WorkshopMetadata>>(
                new Dictionary<string, WorkshopMetadata>(StringComparer.Ordinal));
        }
    }

    private sealed class MainWindowModOperationService : IModOperationService
    {
        public Func<
            IReadOnlyCollection<DownloadRequest>,
            IProgress<OperationProgress>?,
            CancellationToken,
            Task<DownloadBatchResult>>? DownloadHandler
        {
            get;
            set;
        }

        public Func<
            string,
            string,
            bool,
            IProgress<OperationProgress>?,
            CancellationToken,
            Task<DeleteResult>>? DeleteHandler
        {
            get;
            set;
        }

        public int DeleteCallCount { get; private set; }

        public int PermanentDeleteCallCount { get; private set; }

        public int RedownloadCallCount { get; private set; }

        public Task<DownloadBatchResult> DownloadBatchAsync(
            IReadOnlyCollection<DownloadRequest> requests,
            IProgress<OperationProgress>? progress = null,
            CancellationToken cancellationToken = default) =>
            DownloadHandler?.Invoke(requests, progress, cancellationToken)
            ?? Task.FromResult(new DownloadBatchResult
            {
                Results = requests.Select(request => new DownloadResult
                {
                    WorkshopId = request.WorkshopId,
                    Status = OperationStatus.Succeeded,
                    ContentPath = Path.Combine(request.LibraryRoot, request.WorkshopId),
                }).ToArray(),
            });

        public Task<IReadOnlyList<UpdateCheckResult>> CheckUpdatesAsync(
            string libraryRoot,
            IProgress<OperationProgress>? progress = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<DownloadBatchResult> UpdateSelectedAsync(
            string libraryRoot,
            IReadOnlyCollection<string> workshopIds,
            IProgress<OperationProgress>? progress = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<DeleteResult> DeleteAsync(
            string libraryRoot,
            string workshopId,
            bool permanently,
            IProgress<OperationProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            DeleteCallCount++;
            if (permanently)
            {
                PermanentDeleteCallCount++;
            }

            return DeleteHandler?.Invoke(
                libraryRoot,
                workshopId,
                permanently,
                progress,
                cancellationToken)
                ?? Task.FromResult(DeleteResultFor(workshopId, OperationStatus.Succeeded));
        }

        public Task<RedownloadResult> RedownloadAsync(
            string libraryRoot,
            string workshopId,
            bool permanently,
            IProgress<OperationProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            RedownloadCallCount++;
            throw new InvalidOperationException("The main window must use the shared queue.");
        }
    }
}
