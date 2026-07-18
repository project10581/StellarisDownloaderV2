using System.Net.Http;
using StellarisDownloader.App.ViewModels;
using StellarisDownloader.Core.Models;
using StellarisDownloader.Core.Services;

namespace StellarisDownloader.Tests;

public sealed class ApplicationUpdateViewModelTests
{
    [Fact]
    public void CheckMapsAvailableUpdateAndProgress()
    {
        WpfTestRunner.Run(async () =>
        {
            var service = new AppUpdateServiceStub
            {
                CheckHandler = (progress, cancellationToken) =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    progress?.Report(new OperationProgress(
                        "CheckingReleaseFeed",
                        Completed: 1,
                        Total: 1,
                        WorkshopId: null,
                        Message: "Release feed checked."));
                    return Task.FromResult(AvailableUpdate());
                },
            };
            using var viewModel = new ApplicationUpdateViewModel(service);

            await viewModel.CheckAsync();

            Assert.Equal(1, service.CheckCallCount);
            Assert.True(viewModel.HasChecked);
            Assert.Equal("1.0.0", viewModel.CurrentVersion);
            Assert.Equal("2.0.0", viewModel.LatestVersion);
            Assert.Equal("Release notes", viewModel.ReleaseNotes);
            Assert.True(viewModel.IsInstalled);
            Assert.True(viewModel.IsUpdateAvailable);
            Assert.False(viewModel.IsDownloaded);
            Assert.False(viewModel.IsBusy);
            Assert.Equal("CheckCompleted", viewModel.CurrentStage);
            Assert.Equal(1, viewModel.ProgressCompleted);
            Assert.Equal(1, viewModel.ProgressTotal);
            Assert.Equal(100, viewModel.ProgressPercentage);
            Assert.Equal("Release feed checked.", viewModel.ProgressMessage);
            Assert.True(viewModel.CanCheck);
            Assert.True(viewModel.CanDownload);
            Assert.False(viewModel.CanCancel);
            Assert.False(viewModel.CanApplyAndRestart);
            Assert.True(viewModel.CheckCommand.CanExecute(null));
            Assert.True(viewModel.DownloadCommand.CanExecute(null));
            Assert.False(viewModel.CancelCommand.CanExecute(null));
            Assert.False(viewModel.ApplyAndRestartCommand.CanExecute(null));
        });
    }

    [Fact]
    public void NoUpdateDisablesDownloadAndApply()
    {
        WpfTestRunner.Run(async () =>
        {
            var service = new AppUpdateServiceStub();
            using var viewModel = new ApplicationUpdateViewModel(service);

            await viewModel.CheckAsync();

            Assert.True(viewModel.HasChecked);
            Assert.False(viewModel.IsUpdateAvailable);
            Assert.False(viewModel.IsDownloaded);
            Assert.False(viewModel.CanDownload);
            Assert.False(viewModel.CanApplyAndRestart);
            Assert.Contains("up to date", viewModel.StatusMessage, StringComparison.OrdinalIgnoreCase);

            await viewModel.DownloadAsync();
            await viewModel.ApplyAndRestartAsync();

            Assert.Equal(0, service.DownloadCallCount);
            Assert.Equal(0, service.ApplyCallCount);
        });
    }

    [Fact]
    public void DownloadReportsProgressAndNeverAppliesAutomatically()
    {
        WpfTestRunner.Run(async () =>
        {
            var service = new AppUpdateServiceStub
            {
                CheckHandler = (_, _) => Task.FromResult(AvailableUpdate()),
                DownloadHandler = (progress, cancellationToken) =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    progress?.Report(new OperationProgress(
                        "DownloadingApplicationUpdate",
                        Completed: 42,
                        Total: 100,
                        WorkshopId: null,
                        Message: "Downloading 42%."));
                    return Task.FromResult(AvailableUpdate(downloaded: true));
                },
            };
            var prepareCallCount = 0;
            using var viewModel = new ApplicationUpdateViewModel(
                service,
                () =>
                {
                    prepareCallCount++;
                    return Task.CompletedTask;
                });
            await viewModel.CheckAsync();

            await viewModel.DownloadAsync();

            Assert.Equal(1, service.DownloadCallCount);
            Assert.Equal(0, service.ApplyCallCount);
            Assert.Equal(0, prepareCallCount);
            Assert.True(viewModel.IsDownloaded);
            Assert.False(viewModel.IsBusy);
            Assert.Equal("DownloadCompleted", viewModel.CurrentStage);
            Assert.Equal(42, viewModel.ProgressCompleted);
            Assert.Equal(100, viewModel.ProgressTotal);
            Assert.Equal(42, viewModel.ProgressPercentage);
            Assert.Equal("Downloading 42%.", viewModel.ProgressMessage);
            Assert.Null(viewModel.LastError);
            Assert.False(viewModel.CanDownload);
            Assert.True(viewModel.CanApplyAndRestart);
            Assert.True(viewModel.CanDefer);
        });
    }

    [Fact]
    public void CancelStopsActiveDownloadAndRestoresCommandState()
    {
        WpfTestRunner.Run(async () =>
        {
            var entered = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var service = new AppUpdateServiceStub
            {
                CheckHandler = (_, _) => Task.FromResult(AvailableUpdate()),
                DownloadHandler = async (progress, cancellationToken) =>
                {
                    progress?.Report(new OperationProgress(
                        "DownloadingApplicationUpdate",
                        Completed: 10,
                        Total: 100,
                        WorkshopId: null,
                        Message: "Downloading."));
                    entered.SetResult(true);
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    return AvailableUpdate(downloaded: true);
                },
            };
            using var viewModel = new ApplicationUpdateViewModel(service);
            await viewModel.CheckAsync();

            var download = viewModel.DownloadAsync();
            await entered.Task;
            Assert.True(viewModel.IsBusy);
            Assert.False(viewModel.CanCheck);
            Assert.False(viewModel.CanDownload);
            Assert.True(viewModel.CanCancel);
            Assert.False(viewModel.CanApplyAndRestart);
            Assert.False(viewModel.CheckCommand.CanExecute(null));
            Assert.True(viewModel.CancelCommand.CanExecute(null));

            viewModel.Cancel();
            await download;

            Assert.False(viewModel.IsBusy);
            Assert.False(viewModel.CanCancel);
            Assert.Equal("Cancelled", viewModel.CurrentStage);
            Assert.Null(viewModel.LastError);
            Assert.Equal(0, service.ApplyCallCount);
            Assert.True(viewModel.CanDownload);
        });
    }

    [Fact]
    public void DownloadFailureIsExposedWithoutLosingCheckedUpdate()
    {
        WpfTestRunner.Run(async () =>
        {
            var service = new AppUpdateServiceStub
            {
                CheckHandler = (_, _) => Task.FromResult(AvailableUpdate()),
                DownloadHandler = (_, _) => throw new HttpRequestException("Update download failed."),
            };
            using var viewModel = new ApplicationUpdateViewModel(service);
            await viewModel.CheckAsync();

            await viewModel.DownloadAsync();

            Assert.False(viewModel.IsBusy);
            Assert.Equal("Failed", viewModel.CurrentStage);
            Assert.Equal("Update download failed.", viewModel.LastError);
            Assert.Equal("Update download failed.", viewModel.StatusMessage);
            Assert.True(viewModel.HasChecked);
            Assert.True(viewModel.IsUpdateAvailable);
            Assert.False(viewModel.IsDownloaded);
            Assert.True(viewModel.CanDownload);
            Assert.Equal(0, service.ApplyCallCount);
        });
    }

    [Fact]
    public void CustomCheckFailureIsExposedInsteadOfEscapingTheViewModel()
    {
        WpfTestRunner.Run(async () =>
        {
            var service = new AppUpdateServiceStub
            {
                CheckHandler = (_, _) => throw new UpdateTransportException(
                    "The release feed could not be decoded."),
            };
            using var viewModel = new ApplicationUpdateViewModel(service);

            await viewModel.CheckAsync();

            Assert.True(viewModel.HasChecked);
            Assert.False(viewModel.IsBusy);
            Assert.Equal("Failed", viewModel.CurrentStage);
            Assert.Equal("The release feed could not be decoded.", viewModel.LastError);
            Assert.False(viewModel.IsUpdateAvailable);
            Assert.True(viewModel.CanCheck);
            Assert.Equal(0, service.DownloadCallCount);
            Assert.Equal(0, service.ApplyCallCount);
        });
    }

    [Fact]
    public void DeferNeverPreparesOrAppliesDownloadedUpdate()
    {
        WpfTestRunner.Run(async () =>
        {
            var service = new AppUpdateServiceStub
            {
                CheckHandler = (_, _) => Task.FromResult(AvailableUpdate(downloaded: true)),
            };
            var prepareCallCount = 0;
            using var viewModel = new ApplicationUpdateViewModel(
                service,
                () =>
                {
                    prepareCallCount++;
                    return Task.CompletedTask;
                });
            await viewModel.CheckAsync();

            viewModel.Defer();

            Assert.Equal("Deferred", viewModel.CurrentStage);
            Assert.Contains("later", viewModel.StatusMessage, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(0, prepareCallCount);
            Assert.Equal(0, service.ApplyCallCount);
            Assert.True(viewModel.IsDownloaded);
            Assert.True(viewModel.CanApplyAndRestart);
        });
    }

    [Fact]
    public void ExplicitApplyPreparesThenCallsRestartService()
    {
        WpfTestRunner.Run(async () =>
        {
            var calls = new List<string>();
            var service = new AppUpdateServiceStub
            {
                CheckHandler = (_, _) => Task.FromResult(AvailableUpdate(downloaded: true)),
                ApplyHandler = (_, cancellationToken) =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    calls.Add("apply");
                    return Task.CompletedTask;
                },
            };
            using var viewModel = new ApplicationUpdateViewModel(
                service,
                () =>
                {
                    calls.Add("prepare");
                    return Task.CompletedTask;
                });
            await viewModel.CheckAsync();
            Assert.Equal(0, service.ApplyCallCount);

            await viewModel.ApplyAndRestartAsync();

            Assert.Equal(["prepare", "apply"], calls);
            Assert.Equal(1, service.ApplyCallCount);
            Assert.Equal("RestartRequested", viewModel.CurrentStage);
            Assert.Null(viewModel.LastError);
            Assert.False(viewModel.IsBusy);
        });
    }

    [Fact]
    public void PreparationFailurePreventsRestartServiceCall()
    {
        WpfTestRunner.Run(async () =>
        {
            var service = new AppUpdateServiceStub
            {
                CheckHandler = (_, _) => Task.FromResult(AvailableUpdate(downloaded: true)),
            };
            using var viewModel = new ApplicationUpdateViewModel(
                service,
                () => throw new InvalidOperationException("A mod operation is still running."));
            await viewModel.CheckAsync();

            await viewModel.ApplyAndRestartAsync();

            Assert.Equal(0, service.ApplyCallCount);
            Assert.Equal("Failed", viewModel.CurrentStage);
            Assert.Equal("A mod operation is still running.", viewModel.LastError);
            Assert.False(viewModel.IsBusy);
            Assert.True(viewModel.CanApplyAndRestart);
        });
    }

    private static AppUpdateInfo AvailableUpdate(bool downloaded = false) => new()
    {
        CurrentVersion = "1.0.0",
        LatestVersion = "2.0.0",
        ReleaseNotes = "Release notes",
        IsInstalled = true,
        IsUpdateAvailable = true,
        IsDownloaded = downloaded,
    };

    private sealed class AppUpdateServiceStub : IAppUpdateService
    {
        public Func<IProgress<OperationProgress>?, CancellationToken, Task<AppUpdateInfo>>
            CheckHandler
        { get; init; } = (_, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new AppUpdateInfo
            {
                CurrentVersion = "1.0.0",
                LatestVersion = "1.0.0",
                IsInstalled = true,
                IsUpdateAvailable = false,
                IsDownloaded = false,
            });
        };

        public Func<IProgress<OperationProgress>?, CancellationToken, Task<AppUpdateInfo>>
            DownloadHandler
        { get; init; } = (_, _) => throw new NotSupportedException();

        public Func<IProgress<OperationProgress>?, CancellationToken, Task> ApplyHandler
        { get; init; } = (_, _) => Task.CompletedTask;

        public int CheckCallCount { get; private set; }

        public int DownloadCallCount { get; private set; }

        public int ApplyCallCount { get; private set; }

        public Task<AppUpdateInfo> CheckAsync(
            IProgress<OperationProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            CheckCallCount++;
            return CheckHandler(progress, cancellationToken);
        }

        public Task<AppUpdateInfo> DownloadAsync(
            IProgress<OperationProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            DownloadCallCount++;
            return DownloadHandler(progress, cancellationToken);
        }

        public Task ApplyAndRestartAsync(
            IProgress<OperationProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            ApplyCallCount++;
            return ApplyHandler(progress, cancellationToken);
        }
    }

    private sealed class UpdateTransportException(string message) : Exception(message);
}
