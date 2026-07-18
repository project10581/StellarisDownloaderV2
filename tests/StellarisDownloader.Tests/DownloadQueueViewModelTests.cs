using System.Collections.Specialized;
using System.ComponentModel;
using StellarisDownloader.App.ViewModels;
using StellarisDownloader.Core.Models;
using StellarisDownloader.Core.Services;

namespace StellarisDownloader.Tests;

public sealed class DownloadQueueViewModelTests
{
    [Fact]
    public void EnqueueParsesDeduplicatesReportsInvalidAndResolvesTitles()
    {
        WpfTestRunner.Run(async () =>
        {
            var workshop = new QueueWorkshopClient((ids, _) => Task.FromResult<
                IReadOnlyDictionary<string, WorkshopMetadata>>(
                new Dictionary<string, WorkshopMetadata>(StringComparer.Ordinal)
                {
                    ["123"] = Metadata("123", "First title"),
                    ["456"] = Metadata("456", "Second title"),
                }));
            using var viewModel = CreateViewModel(workshop: workshop);

            var first = await viewModel.EnqueueAsync(
                "123\r\nnot-a-workshop\r\n"
                + "https://steamcommunity.com/sharedfiles/filedetails/?id=456\r\n123");
            var second = await viewModel.EnqueueAsync("456");

            Assert.Equal(2, first.AddedCount);
            Assert.Equal(["123", "456"], first.AddedWorkshopIds);
            Assert.Single(first.InvalidInputs);
            Assert.Equal(2, first.InvalidInputs[0].LineNumber);
            Assert.Equal(0, first.DuplicateCount);
            Assert.Equal(0, second.AddedCount);
            Assert.Equal(1, second.DuplicateCount);
            Assert.Equal(["123", "456"], workshop.Requests.Single());
            Assert.Equal(["First title", "Second title"], viewModel.Items.Select(item => item.Title));
            Assert.All(
                viewModel.Items,
                item => Assert.Equal(DownloadQueueItemStatus.Ready, item.Status));
            Assert.Empty(viewModel.InvalidInputs);
        });
    }

    [Fact]
    public void MetadataFailureLeavesItemsReadyForDownload()
    {
        WpfTestRunner.Run(async () =>
        {
            var workshop = new QueueWorkshopClient((_, _) =>
                throw new HttpRequestException("Offline."));
            using var viewModel = CreateViewModel(workshop: workshop);

            await viewModel.EnqueueAsync("123");

            var item = Assert.Single(viewModel.Items);
            Assert.Equal(DownloadQueueItemStatus.Ready, item.Status);
            Assert.Equal("123", item.DisplayTitle);
            Assert.Contains("Offline.", item.MetadataError, StringComparison.Ordinal);
            Assert.True(viewModel.CanStart);
        });
    }

    [Fact]
    public void OneSharedInstanceDeduplicatesInputFromMultipleConsumers()
    {
        WpfTestRunner.Run(async () =>
        {
            using var sharedQueue = CreateViewModel();
            var downloadWindowQueue = sharedQueue;
            var browserWindowQueue = sharedQueue;

            var first = await downloadWindowQueue.EnqueueAsync("123");
            var second = await browserWindowQueue.EnqueueAsync("123\n456");

            Assert.Equal(1, first.AddedCount);
            Assert.Equal(1, second.AddedCount);
            Assert.Equal(1, second.DuplicateCount);
            Assert.Equal(["123", "456"], sharedQueue.Items.Select(item => item.WorkshopId));
        });
    }

    [Fact]
    public void StartUsesConfiguredLibraryReportsProgressAndCountsEveryResult()
    {
        WpfTestRunner.Run(async () =>
        {
            IReadOnlyList<DownloadRequest>? capturedRequests = null;
            var operations = new QueueModOperationService((requests, progress, _) =>
            {
                capturedRequests = requests.ToArray();
                progress?.Report(new OperationProgress(
                    "DownloadingQueue",
                    Completed: 0,
                    Total: 3,
                    WorkshopId: "100",
                    Message: "Queue item one."));
                progress?.Report(new OperationProgress(
                    "ProcessOutput",
                    Completed: 0,
                    Total: 0,
                    WorkshopId: null,
                    Message: "SteamCMD output."));
                return Task.FromResult(new DownloadBatchResult
                {
                    Results =
                    [
                        Result("100", OperationStatus.Succeeded),
                        Result("200", OperationStatus.Failed, "Failed item."),
                        Result("300", OperationStatus.Cancelled, "Cancelled item."),
                    ],
                });
            });
            using var viewModel = CreateViewModel(operations: operations);
            await viewModel.EnqueueAsync("100\n200\n300");

            await viewModel.StartAsync();

            Assert.NotNull(capturedRequests);
            Assert.All(capturedRequests, request => Assert.Equal("C:\\Mods", request.LibraryRoot));
            Assert.Equal(
                [
                    DownloadQueueItemStatus.Succeeded,
                    DownloadQueueItemStatus.Failed,
                    DownloadQueueItemStatus.Cancelled,
                ],
                viewModel.Items.Select(item => item.Status));
            Assert.Equal(1, viewModel.SucceededCount);
            Assert.Equal(1, viewModel.FailedCount);
            Assert.Equal(1, viewModel.CancelledCount);
            Assert.Equal(3, viewModel.ProgressCompleted);
            Assert.Equal(3, viewModel.ProgressTotal);
            Assert.Equal("Completed", viewModel.CurrentStage);
            Assert.Contains("SteamCMD output.", viewModel.Logs);
            Assert.False(viewModel.IsBusy);
        });
    }

    [Fact]
    public void CancelSignalsTheActiveBatchAndCancelledItemsCanStartAgain()
    {
        WpfTestRunner.Run(async () =>
        {
            var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var operations = new QueueModOperationService(async (requests, _, cancellationToken) =>
            {
                entered.SetResult(true);
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    // The result mirrors the Core queue's cancellation contract.
                }

                return new DownloadBatchResult
                {
                    Results = requests
                        .Select(request => Result(
                            request.WorkshopId,
                            OperationStatus.Cancelled,
                            "Cancelled."))
                        .ToArray(),
                };
            });
            using var viewModel = CreateViewModel(operations: operations);
            await viewModel.EnqueueAsync("100\n200");

            var run = viewModel.StartAsync();
            await entered.Task;
            Assert.True(viewModel.Clear() is false);
            Assert.Equal(0, viewModel.Remove(["100"]));
            viewModel.Cancel();
            await run;

            Assert.Equal(2, viewModel.CancelledCount);
            Assert.All(
                viewModel.Items,
                item => Assert.Equal(DownloadQueueItemStatus.Cancelled, item.Status));
            Assert.True(viewModel.CanStart);
            Assert.Contains("Cancellation requested.", viewModel.Logs);
        });
    }

    [Fact]
    public void RetryFailedRequeuesOnlyFailuresAndASecondRunCanSucceed()
    {
        WpfTestRunner.Run(async () =>
        {
            var callCount = 0;
            var operations = new QueueModOperationService((requests, _, _) =>
            {
                callCount++;
                var status = callCount == 1 ? OperationStatus.Failed : OperationStatus.Succeeded;
                return Task.FromResult(new DownloadBatchResult
                {
                    Results = requests.Select(request => Result(request.WorkshopId, status)).ToArray(),
                });
            });
            using var viewModel = CreateViewModel(operations: operations);
            await viewModel.EnqueueAsync("100");
            await viewModel.StartAsync();

            var retryCount = await viewModel.RetryFailedAsync();

            Assert.Equal(1, retryCount);
            Assert.Equal(DownloadQueueItemStatus.Ready, viewModel.Items[0].Status);
            Assert.Equal(0, viewModel.FailedCount);
            await viewModel.StartAsync();
            Assert.Equal(DownloadQueueItemStatus.Succeeded, viewModel.Items[0].Status);
            Assert.Equal(1, viewModel.SucceededCount);
            Assert.Equal(2, callCount);
        });
    }

    [Fact]
    public void RemoveAndClearUpdateQueueStateWhenIdle()
    {
        WpfTestRunner.Run(async () =>
        {
            using var viewModel = CreateViewModel();
            await viewModel.EnqueueAsync("100\n200");

            var removed = viewModel.Remove(["100", "missing"]);

            Assert.Equal(1, removed);
            Assert.Equal("200", Assert.Single(viewModel.Items).WorkshopId);
            Assert.True(viewModel.Clear());
            Assert.Empty(viewModel.Items);
            Assert.Empty(viewModel.Logs);
            Assert.False(viewModel.CanStart);
        });
    }

    [Fact]
    public void BackgroundMetadataAndProgressCallbacksMutateCollectionsOnTheUiThread()
    {
        WpfTestRunner.Run(async () =>
        {
            var uiThreadId = Environment.CurrentManagedThreadId;
            var metadataEntered = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var metadataRelease = new TaskCompletionSource<IReadOnlyDictionary<string, WorkshopMetadata>>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var workshop = new QueueWorkshopClient(async (_, _) =>
            {
                metadataEntered.SetResult(true);
                return await metadataRelease.Task.ConfigureAwait(false);
            });
            var operations = new QueueModOperationService(async (requests, progress, cancellationToken) =>
            {
                await Task.Run(() => progress?.Report(new OperationProgress(
                    "DownloadingQueue",
                    Completed: 0,
                    Total: 1,
                    WorkshopId: requests.Single().WorkshopId,
                    Message: "Background progress.")), cancellationToken);
                return new DownloadBatchResult
                {
                    Results = [Result(requests.Single().WorkshopId, OperationStatus.Succeeded)],
                };
            });
            using var viewModel = CreateViewModel(workshop: workshop, operations: operations);
            var collectionThreadIds = new List<int>();
            var propertyThreadIds = new List<int>();
            ((INotifyCollectionChanged)viewModel.Items).CollectionChanged += (_, _) =>
                collectionThreadIds.Add(Environment.CurrentManagedThreadId);

            var enqueue = viewModel.EnqueueAsync("100");
            await metadataEntered.Task;
            viewModel.Items[0].PropertyChanged += (_, args) => RecordPropertyThread(
                args,
                propertyThreadIds);
            await Task.Run(() => metadataRelease.SetResult(
                new Dictionary<string, WorkshopMetadata>
                {
                    ["100"] = Metadata("100", "Background title"),
                }));
            await enqueue;
            await viewModel.StartAsync();

            Assert.NotEmpty(collectionThreadIds);
            Assert.NotEmpty(propertyThreadIds);
            Assert.All(collectionThreadIds, threadId => Assert.Equal(uiThreadId, threadId));
            Assert.All(propertyThreadIds, threadId => Assert.Equal(uiThreadId, threadId));
        });
    }

    private static DownloadQueueViewModel CreateViewModel(
        QueueWorkshopClient? workshop = null,
        QueueModOperationService? operations = null) =>
        new(
            new StubSettingsStore(new AppSettings { LibraryRoot = "C:\\Mods" }),
            workshop ?? new QueueWorkshopClient(),
            operations ?? new QueueModOperationService());

    private static WorkshopMetadata Metadata(string id, string title) => new()
    {
        WorkshopId = id,
        Title = title,
    };

    private static DownloadResult Result(
        string id,
        OperationStatus status,
        string? error = null) => new()
        {
            WorkshopId = id,
            Status = status,
            ContentPath = Path.Combine("C:\\Mods", id),
            Error = error,
        };

    private static void RecordPropertyThread(
        PropertyChangedEventArgs args,
        List<int> threadIds)
    {
        if (args.PropertyName is nameof(DownloadQueueItemViewModel.Title)
            or nameof(DownloadQueueItemViewModel.Status))
        {
            threadIds.Add(Environment.CurrentManagedThreadId);
        }
    }

    private sealed class QueueWorkshopClient : IWorkshopClient
    {
        private readonly Func<
            IReadOnlyCollection<string>,
            CancellationToken,
            Task<IReadOnlyDictionary<string, WorkshopMetadata>>> getMetadata;

        public QueueWorkshopClient()
            : this((_, _) => Task.FromResult<IReadOnlyDictionary<string, WorkshopMetadata>>(
                new Dictionary<string, WorkshopMetadata>(StringComparer.Ordinal)))
        {
        }

        public QueueWorkshopClient(
            Func<
                IReadOnlyCollection<string>,
                CancellationToken,
                Task<IReadOnlyDictionary<string, WorkshopMetadata>>> getMetadata)
        {
            this.getMetadata = getMetadata;
        }

        public List<IReadOnlyList<string>> Requests { get; } = [];

        public Task<IReadOnlyDictionary<string, WorkshopMetadata>> GetMetadataBatchAsync(
            IReadOnlyCollection<string> workshopIds,
            IProgress<OperationProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(workshopIds.ToArray());
            return getMetadata(workshopIds, cancellationToken);
        }
    }

    private sealed class QueueModOperationService : IModOperationService
    {
        private readonly Func<
            IReadOnlyCollection<DownloadRequest>,
            IProgress<OperationProgress>?,
            CancellationToken,
            Task<DownloadBatchResult>> download;

        public QueueModOperationService()
            : this((requests, _, _) => Task.FromResult(new DownloadBatchResult
            {
                Results = requests
                    .Select(request => Result(request.WorkshopId, OperationStatus.Succeeded))
                    .ToArray(),
            }))
        {
        }

        public QueueModOperationService(
            Func<
                IReadOnlyCollection<DownloadRequest>,
                IProgress<OperationProgress>?,
                CancellationToken,
                Task<DownloadBatchResult>> download)
        {
            this.download = download;
        }

        public Task<DownloadBatchResult> DownloadBatchAsync(
            IReadOnlyCollection<DownloadRequest> requests,
            IProgress<OperationProgress>? progress = null,
            CancellationToken cancellationToken = default) =>
            download(requests, progress, cancellationToken);

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
