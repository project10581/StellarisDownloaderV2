using StellarisDownloader.App.ViewModels;
using StellarisDownloader.Core.Models;
using StellarisDownloader.Core.Services;

namespace StellarisDownloader.Tests;

public sealed class UpdateSelectionViewModelTests
{
    [Fact]
    public void CheckDisplaysEveryResultAndSelectsOnlyAvailableUpdatesByDefault()
    {
        WpfTestRunner.Run(async () =>
        {
            var remoteTime = new DateTimeOffset(2026, 7, 17, 10, 0, 0, TimeSpan.Zero);
            var installedTime = remoteTime.AddDays(-2);
            var operations = new UpdateOperationStub
            {
                CheckHandler = (_, _, _) => Task.FromResult<IReadOnlyList<UpdateCheckResult>>(
                [
                    CheckResult(
                        "100",
                        "Available mod",
                        UpdateState.UpdateAvailable,
                        remoteTime,
                        installedTime,
                        usesApproximateTimestamp: true),
                    CheckResult("200", "Current mod", UpdateState.UpToDate),
                    CheckResult(
                        "300",
                        "Failed mod",
                        UpdateState.CheckFailed,
                        error: "Workshop metadata is unavailable."),
                    CheckResult("400", null, UpdateState.Unknown),
                ]),
            };
            using var viewModel = CreateViewModel(operations);

            await viewModel.CheckUpdatesAsync();

            Assert.Equal(4, viewModel.Items.Count);
            var available = viewModel.Items[0];
            Assert.Equal("Available mod", available.DisplayTitle);
            Assert.Equal(remoteTime, available.LatestRemoteUpdatedAtUtc);
            Assert.Equal(installedTime, available.InstalledWorkshopUpdatedAtUtc);
            Assert.True(available.UsesApproximateLocalTimestamp);
            Assert.True(available.IsSelected);
            Assert.True(available.IsSelectionEnabled);

            Assert.False(viewModel.Items[1].IsSelected);
            Assert.False(viewModel.Items[2].IsSelected);
            Assert.False(viewModel.Items[2].IsSelectionEnabled);
            Assert.Equal("Workshop metadata is unavailable.", viewModel.Items[2].Error);
            Assert.False(viewModel.Items[3].IsSelected);
            Assert.Equal("400", viewModel.Items[3].DisplayTitle);
            Assert.Equal(1, viewModel.SelectedCount);
            Assert.True(viewModel.CanUpdateSelected);
            Assert.Equal("CheckCompleted", viewModel.CurrentStage);
        });
    }

    [Fact]
    public void SelectAllAvailableDoesNotSelectFailedOrCurrentItems()
    {
        WpfTestRunner.Run(async () =>
        {
            var operations = new UpdateOperationStub
            {
                CheckHandler = (_, _, _) => Task.FromResult<IReadOnlyList<UpdateCheckResult>>(
                [
                    CheckResult("100", "One", UpdateState.UpdateAvailable),
                    CheckResult("200", "Two", UpdateState.UpdateAvailable),
                    CheckResult("300", "Failed", UpdateState.CheckFailed, error: "Offline."),
                    CheckResult("400", "Current", UpdateState.UpToDate),
                ]),
            };
            using var viewModel = CreateViewModel(operations);
            await viewModel.CheckUpdatesAsync();
            viewModel.Items[0].IsSelected = false;
            viewModel.Items[2].IsSelected = true;

            viewModel.SelectAllAvailable();

            Assert.True(viewModel.Items[0].IsSelected);
            Assert.True(viewModel.Items[1].IsSelected);
            Assert.False(viewModel.Items[2].IsSelected);
            Assert.False(viewModel.Items[3].IsSelected);
            Assert.Equal(2, viewModel.SelectedCount);
            Assert.False(viewModel.CanSelectAllAvailable);
        });
    }

    [Fact]
    public void UpdateSelectedMakesOneUniqueBackendCallAndAppliesProgressAndResults()
    {
        WpfTestRunner.Run(async () =>
        {
            var operations = new UpdateOperationStub
            {
                CheckHandler = (_, _, _) => Task.FromResult<IReadOnlyList<UpdateCheckResult>>(
                [
                    CheckResult("100", "Duplicate one", UpdateState.UpdateAvailable),
                    CheckResult("100", "Duplicate two", UpdateState.UpdateAvailable),
                    CheckResult("200", "Will fail", UpdateState.UpdateAvailable),
                ]),
                UpdateHandler = (ids, progress, _) =>
                {
                    progress?.Report(new OperationProgress(
                        "DownloadingQueue",
                        Completed: 1,
                        Total: 2,
                        WorkshopId: "200",
                        Message: "Updating the second selected mod."));
                    return Task.FromResult(new DownloadBatchResult
                    {
                        Results =
                        [
                            DownloadResult("100", OperationStatus.Succeeded),
                            DownloadResult("200", OperationStatus.Failed, "SteamCMD failed."),
                        ],
                    });
                },
            };
            using var viewModel = CreateViewModel(operations);
            await viewModel.CheckUpdatesAsync();

            await viewModel.UpdateSelectedAsync();

            Assert.Equal(1, operations.UpdateCallCount);
            Assert.Equal("C:\\Mods", operations.UpdateLibraryRoots.Single());
            Assert.Equal(["100", "200"], operations.UpdateRequests.Single());
            Assert.Equal(1, viewModel.SucceededCount);
            Assert.Equal(1, viewModel.FailedCount);
            Assert.Equal(0, viewModel.CancelledCount);
            Assert.Equal("SteamCMD failed.", viewModel.LastError);
            Assert.Equal("Completed", viewModel.CurrentStage);
            Assert.Equal(1, viewModel.ProgressCompleted);
            Assert.Equal(2, viewModel.ProgressTotal);
            Assert.Equal("200", viewModel.CurrentWorkshopId);
            Assert.Equal("Updating the second selected mod.", viewModel.ProgressMessage);
            Assert.All(
                viewModel.Items.Where(item => item.WorkshopId == "100"),
                item =>
                {
                    Assert.Equal(OperationStatus.Succeeded, item.LastOperationStatus);
                    Assert.False(item.IsSelected);
                });
            var failed = Assert.Single(viewModel.Items, item => item.WorkshopId == "200");
            Assert.Equal(OperationStatus.Failed, failed.LastOperationStatus);
            Assert.Equal("SteamCMD failed.", failed.LastOperationError);
            Assert.True(failed.IsSelected);
        });
    }

    [Fact]
    public void CancelStopsTheActiveUpdateAndCommandsRemainMutuallyExclusive()
    {
        WpfTestRunner.Run(async () =>
        {
            var entered = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var operations = new UpdateOperationStub
            {
                CheckHandler = (_, _, _) => Task.FromResult<IReadOnlyList<UpdateCheckResult>>(
                [CheckResult("100", "Cancelable", UpdateState.UpdateAvailable)]),
                UpdateHandler = async (_, progress, cancellationToken) =>
                {
                    progress?.Report(new OperationProgress(
                        "RunningSteamCmd",
                        Completed: 0,
                        Total: 1,
                        WorkshopId: "100",
                        Message: "SteamCMD is running."));
                    entered.SetResult(true);
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    return new DownloadBatchResult();
                },
            };
            using var viewModel = CreateViewModel(operations);
            await viewModel.CheckUpdatesAsync();

            var update = viewModel.UpdateSelectedAsync();
            await entered.Task;
            Assert.True(viewModel.IsBusy);
            Assert.False(viewModel.CanCheckUpdates);
            Assert.False(viewModel.CheckUpdatesCommand.CanExecute(null));
            Assert.False(viewModel.UpdateSelectedCommand.CanExecute(null));
            Assert.True(viewModel.CancelCommand.CanExecute(null));

            viewModel.Cancel();
            await update;

            Assert.False(viewModel.IsBusy);
            Assert.Equal(0, viewModel.SucceededCount);
            Assert.Equal(0, viewModel.FailedCount);
            Assert.Equal(1, viewModel.CancelledCount);
            Assert.Equal("Cancelled", viewModel.CurrentStage);
            Assert.Equal("SteamCMD is running.", viewModel.ProgressMessage);
            Assert.Equal(OperationStatus.Cancelled, viewModel.Items[0].LastOperationStatus);
            Assert.True(viewModel.CanCheckUpdates);
        });
    }

    [Fact]
    public void MissingLibraryIsReportedWithoutCallingTheOperationService()
    {
        WpfTestRunner.Run(async () =>
        {
            var operations = new UpdateOperationStub();
            using var viewModel = new UpdateSelectionViewModel(
                new UpdateSettingsStore(
                    new AppSettings { LibraryRoot = null },
                    requiresInitialization: true),
                operations);

            await viewModel.CheckUpdatesAsync();

            Assert.Equal(0, operations.CheckCallCount);
            Assert.Empty(viewModel.Items);
            Assert.Equal("Failed", viewModel.CurrentStage);
            Assert.Contains("Choose a mod library", viewModel.LastError, StringComparison.Ordinal);
            Assert.False(viewModel.CanUpdateSelected);
        });
    }

    [Fact]
    public void EmptyServiceResultExplicitlyDescribesEmptyOrInvalidCacheState()
    {
        WpfTestRunner.Run(async () =>
        {
            var operations = new UpdateOperationStub();
            using var viewModel = CreateViewModel(operations);

            await viewModel.CheckUpdatesAsync();

            Assert.Empty(viewModel.Items);
            Assert.Null(viewModel.LastError);
            Assert.Contains("empty, stale", viewModel.StatusMessage, StringComparison.Ordinal);
            Assert.Contains("different library root", viewModel.StatusMessage, StringComparison.Ordinal);
            Assert.False(viewModel.CanUpdateSelected);
        });
    }

    [Fact]
    public void CheckFailureIsExposedInsteadOfBeingReportedAsUpToDate()
    {
        WpfTestRunner.Run(async () =>
        {
            var operations = new UpdateOperationStub
            {
                CheckHandler = (_, _, _) => throw new InvalidOperationException(
                    "The cache belongs to another library root."),
            };
            using var viewModel = CreateViewModel(operations);

            await viewModel.CheckUpdatesAsync();

            Assert.Empty(viewModel.Items);
            Assert.Equal("Failed", viewModel.CurrentStage);
            Assert.Equal("The cache belongs to another library root.", viewModel.LastError);
            Assert.False(viewModel.CanUpdateSelected);
        });
    }

    private static UpdateSelectionViewModel CreateViewModel(UpdateOperationStub operations) =>
        new(
            new UpdateSettingsStore(new AppSettings { LibraryRoot = "C:\\Mods" }),
            operations);

    private static UpdateCheckResult CheckResult(
        string id,
        string? title,
        UpdateState state,
        DateTimeOffset? remoteTime = null,
        DateTimeOffset? installedTime = null,
        bool usesApproximateTimestamp = false,
        string? error = null) => new()
        {
            WorkshopId = id,
            Title = title,
            State = state,
            LatestRemoteUpdatedAtUtc = remoteTime,
            InstalledWorkshopUpdatedAtUtc = installedTime,
            UsesApproximateLocalTimestamp = usesApproximateTimestamp,
            Error = error,
        };

    private static DownloadResult DownloadResult(
        string id,
        OperationStatus status,
        string? error = null) => new()
        {
            WorkshopId = id,
            Status = status,
            ContentPath = Path.Combine("C:\\Mods", id),
            Error = error,
        };

    private sealed class UpdateSettingsStore(
        AppSettings settings,
        bool requiresInitialization = false) : ISettingsStore
    {
        public Task<SettingsLoadResult> LoadAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new SettingsLoadResult(
                settings,
                requiresInitialization,
                CorruptBackupPath: null));
        }

        public Task SaveAsync(
            AppSettings settings,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class UpdateOperationStub : IModOperationService
    {
        public Func<
            string,
            IProgress<OperationProgress>?,
            CancellationToken,
            Task<IReadOnlyList<UpdateCheckResult>>>
            CheckHandler
        { get; init; } =
                (_, _, _) => Task.FromResult<IReadOnlyList<UpdateCheckResult>>([]);

        public Func<
            IReadOnlyCollection<string>,
            IProgress<OperationProgress>?,
            CancellationToken,
            Task<DownloadBatchResult>>
            UpdateHandler
        { get; init; } =
                (_, _, _) => Task.FromResult(new DownloadBatchResult());

        public int CheckCallCount { get; private set; }

        public int UpdateCallCount { get; private set; }

        public List<string> UpdateLibraryRoots { get; } = [];

        public List<IReadOnlyList<string>> UpdateRequests { get; } = [];

        public Task<DownloadBatchResult> DownloadBatchAsync(
            IReadOnlyCollection<DownloadRequest> requests,
            IProgress<OperationProgress>? progress = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<UpdateCheckResult>> CheckUpdatesAsync(
            string libraryRoot,
            IProgress<OperationProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            CheckCallCount++;
            return CheckHandler(libraryRoot, progress, cancellationToken);
        }

        public Task<DownloadBatchResult> UpdateSelectedAsync(
            string libraryRoot,
            IReadOnlyCollection<string> workshopIds,
            IProgress<OperationProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            UpdateCallCount++;
            UpdateLibraryRoots.Add(libraryRoot);
            UpdateRequests.Add(workshopIds.ToArray());
            return UpdateHandler(workshopIds, progress, cancellationToken);
        }

        public Task<DeleteResult> DeleteAsync(
            string libraryRoot,
            string workshopId,
            bool permanently,
            IProgress<OperationProgress>? progress = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<RedownloadResult> RedownloadAsync(
            string libraryRoot,
            string workshopId,
            bool permanently,
            IProgress<OperationProgress>? progress = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
