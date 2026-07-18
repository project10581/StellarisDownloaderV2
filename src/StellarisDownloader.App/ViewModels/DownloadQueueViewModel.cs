using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StellarisDownloader.Core.Models;
using StellarisDownloader.Core.Services;

namespace StellarisDownloader.App.ViewModels;

public sealed class DownloadQueueViewModel : ObservableObject, IDisposable
{
    private readonly ISettingsStore settingsStore;
    private readonly IWorkshopClient workshopClient;
    private readonly IModOperationService modOperationService;
    private readonly SynchronizationContext uiContext;
    private readonly int uiThreadId;
    private readonly SemaphoreSlim enqueueGate = new(1, 1);
    private readonly ObservableCollection<DownloadQueueItemViewModel> items = [];
    private readonly ObservableCollection<InvalidWorkshopInput> invalidInputs = [];
    private readonly ObservableCollection<string> logs = [];
    private readonly AsyncRelayCommand enqueueCommand;
    private readonly AsyncRelayCommand startCommand;
    private readonly RelayCommand cancelCommand;
    private readonly AsyncRelayCommand retryFailedCommand;
    private CancellationTokenSource? activeCancellation;
    private string inputText = string.Empty;
    private bool isBusy;
    private string? currentStage;
    private string? currentWorkshopId;
    private int progressCompleted;
    private int progressTotal;
    private int succeededCount;
    private int failedCount;
    private int cancelledCount;
    private string? lastError;
    private bool disposed;

    public DownloadQueueViewModel(
        ISettingsStore settingsStore,
        IWorkshopClient workshopClient,
        IModOperationService modOperationService,
        SynchronizationContext? uiContext = null)
    {
        ArgumentNullException.ThrowIfNull(settingsStore);
        ArgumentNullException.ThrowIfNull(workshopClient);
        ArgumentNullException.ThrowIfNull(modOperationService);

        this.settingsStore = settingsStore;
        this.workshopClient = workshopClient;
        this.modOperationService = modOperationService;
        this.uiContext = uiContext
            ?? SynchronizationContext.Current
            ?? new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher);
        uiThreadId = Environment.CurrentManagedThreadId;

        Items = new ReadOnlyObservableCollection<DownloadQueueItemViewModel>(items);
        InvalidInputs = new ReadOnlyObservableCollection<InvalidWorkshopInput>(invalidInputs);
        Logs = new ReadOnlyObservableCollection<string>(logs);

        enqueueCommand = new AsyncRelayCommand(EnqueueInputTextAsync, CanEnqueue);
        startCommand = new AsyncRelayCommand(
            () => StartAsync(CancellationToken.None),
            () => CanStart);
        cancelCommand = new RelayCommand(Cancel, () => CanCancel);
        retryFailedCommand = new AsyncRelayCommand(
            RetryFailedCommandAsync,
            () => !IsBusy && items.Any(item => item.Status == DownloadQueueItemStatus.Failed));
    }

    public ReadOnlyObservableCollection<DownloadQueueItemViewModel> Items { get; }

    public ReadOnlyObservableCollection<InvalidWorkshopInput> InvalidInputs { get; }

    public ReadOnlyObservableCollection<string> Logs { get; }

    public IAsyncRelayCommand EnqueueCommand => enqueueCommand;

    public IAsyncRelayCommand StartCommand => startCommand;

    public IRelayCommand CancelCommand => cancelCommand;

    public IAsyncRelayCommand RetryFailedCommand => retryFailedCommand;

    public string InputText
    {
        get => inputText;
        set
        {
            if (SetProperty(ref inputText, value ?? string.Empty))
            {
                enqueueCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool IsBusy
    {
        get => isBusy;
        private set
        {
            if (SetProperty(ref isBusy, value))
            {
                OnPropertyChanged(nameof(CanStart));
                OnPropertyChanged(nameof(CanCancel));
                NotifyCommandStates();
            }
        }
    }

    public bool CanStart => !IsBusy && items.Any(IsStartable);

    public bool CanCancel => IsBusy
        && activeCancellation is not null
        && !activeCancellation.IsCancellationRequested;

    public string? CurrentStage
    {
        get => currentStage;
        private set => SetProperty(ref currentStage, value);
    }

    public string? CurrentWorkshopId
    {
        get => currentWorkshopId;
        private set => SetProperty(ref currentWorkshopId, value);
    }

    public int ProgressCompleted
    {
        get => progressCompleted;
        private set => SetProperty(ref progressCompleted, value);
    }

    public int ProgressTotal
    {
        get => progressTotal;
        private set => SetProperty(ref progressTotal, value);
    }

    public int SucceededCount
    {
        get => succeededCount;
        private set => SetProperty(ref succeededCount, value);
    }

    public int FailedCount
    {
        get => failedCount;
        private set => SetProperty(ref failedCount, value);
    }

    public int CancelledCount
    {
        get => cancelledCount;
        private set => SetProperty(ref cancelledCount, value);
    }

    public string? LastError
    {
        get => lastError;
        private set => SetProperty(ref lastError, value);
    }

    public async Task<DownloadQueueEnqueueResult> EnqueueAsync(
        string? input,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var parsed = WorkshopIdParser.Parse(input);
        await enqueueGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = await InvokeOnUiAsync(() =>
            {
                invalidInputs.Clear();
                foreach (var invalidInput in parsed.InvalidInputs)
                {
                    invalidInputs.Add(invalidInput);
                }

                var existingIds = items
                    .Select(item => item.WorkshopId)
                    .ToHashSet(StringComparer.Ordinal);
                var addedItems = new List<DownloadQueueItemViewModel>();
                var duplicates = 0;
                foreach (var workshopId in parsed.WorkshopIds)
                {
                    if (!existingIds.Add(workshopId))
                    {
                        duplicates++;
                        continue;
                    }

                    var item = new DownloadQueueItemViewModel(workshopId);
                    item.BeginMetadataLookup();
                    items.Add(item);
                    addedItems.Add(item);
                }

                if (addedItems.Count > 0)
                {
                    logs.Add($"Added {addedItems.Count} Workshop item(s) to the download queue.");
                }

                NotifyQueueStateChanged();
                return (Items: addedItems, DuplicateCount: duplicates);
            }).ConfigureAwait(false);

            if (state.Items.Count > 0)
            {
                await ResolveMetadataAsync(state.Items, cancellationToken).ConfigureAwait(false);
            }

            return new DownloadQueueEnqueueResult(
                state.Items.Select(item => item.WorkshopId).ToArray(),
                state.DuplicateCount,
                parsed.InvalidInputs);
        }
        finally
        {
            enqueueGate.Release();
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var batch = await InvokeOnUiAsync(() =>
        {
            if (IsBusy)
            {
                return Array.Empty<DownloadQueueItemViewModel>();
            }

            var candidates = items.Where(IsStartable).ToArray();
            if (candidates.Length == 0)
            {
                return candidates;
            }

            activeCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            IsBusy = true;
            LastError = null;
            CurrentStage = "PreparingDownloadQueue";
            CurrentWorkshopId = null;
            ProgressCompleted = 0;
            ProgressTotal = candidates.Length;
            foreach (var item in candidates)
            {
                item.MarkReady();
            }

            logs.Add($"Starting download queue with {candidates.Length} item(s).");
            NotifyQueueStateChanged();
            return candidates;
        }).ConfigureAwait(false);
        if (batch.Length == 0)
        {
            return;
        }

        var cancellation = activeCancellation
            ?? throw new InvalidOperationException("The queue cancellation source was not created.");
        var pendingProgress = new ConcurrentQueue<Task>();
        var progress = new CallbackProgress<OperationProgress>(value =>
        {
            pendingProgress.Enqueue(InvokeOnUiAsync(() => ApplyProgress(value, batch)));
        });
        DownloadBatchResult? batchResult = null;
        string? batchFailure = null;
        var wasCancelled = false;
        try
        {
            var settings = await settingsStore.LoadAsync(cancellation.Token).ConfigureAwait(false);
            if (settings.RequiresInitialization || string.IsNullOrWhiteSpace(settings.Settings.LibraryRoot))
            {
                batchFailure = "Choose a mod library folder before starting downloads.";
            }
            else
            {
                var requests = batch.Select(item => new DownloadRequest
                {
                    WorkshopId = item.WorkshopId,
                    LibraryRoot = settings.Settings.LibraryRoot,
                }).ToArray();
                batchResult = await modOperationService.DownloadBatchAsync(
                    requests,
                    progress,
                    cancellation.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            wasCancelled = true;
        }
        catch (Exception exception) when (IsOperationalFailure(exception))
        {
            batchFailure = exception.Message;
        }
        finally
        {
            await DrainProgressAsync(pendingProgress).ConfigureAwait(false);
            await InvokeOnUiAsync(() =>
            {
                if (batchResult is not null)
                {
                    ApplyResults(batch, batchResult);
                }
                else if (wasCancelled || cancellation.IsCancellationRequested)
                {
                    const string error = "Download queue was cancelled.";
                    foreach (var item in batch)
                    {
                        item.Cancel(error);
                    }

                    logs.Add(error);
                    CurrentStage = "Cancelled";
                }
                else
                {
                    var error = batchFailure ?? "The download queue failed.";
                    foreach (var item in batch)
                    {
                        item.Fail(error);
                    }

                    LastError = error;
                    logs.Add(error);
                    CurrentStage = "Failed";
                }

                ProgressCompleted = batch.Length;
                ProgressTotal = batch.Length;
                CurrentWorkshopId = null;
                activeCancellation = null;
                IsBusy = false;
                RecalculateCounts();
                NotifyQueueStateChanged();
            }).ConfigureAwait(false);
            cancellation.Dispose();
        }
    }

    public void Cancel()
    {
        ThrowIfDisposed();
        var cancellation = activeCancellation;
        if (cancellation is null || cancellation.IsCancellationRequested)
        {
            return;
        }

        cancellation.Cancel();
        _ = InvokeOnUiAsync(() =>
        {
            logs.Add("Cancellation requested.");
            cancelCommand.NotifyCanExecuteChanged();
        });
    }

    public async Task<int> RetryFailedAsync()
    {
        ThrowIfDisposed();
        return await InvokeOnUiAsync(() =>
        {
            if (IsBusy)
            {
                return 0;
            }

            var retryCount = 0;
            foreach (var item in items.Where(item => item.Status == DownloadQueueItemStatus.Failed))
            {
                item.MarkReady();
                retryCount++;
            }

            if (retryCount > 0)
            {
                logs.Add($"Returned {retryCount} failed item(s) to the download queue.");
            }

            RecalculateCounts();
            NotifyQueueStateChanged();
            return retryCount;
        }).ConfigureAwait(false);
    }

    public int Remove(IEnumerable<string> workshopIds)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(workshopIds);
        VerifyUiThread();
        if (IsBusy)
        {
            return 0;
        }

        var ids = workshopIds.ToHashSet(StringComparer.Ordinal);
        var removed = 0;
        for (var index = items.Count - 1; index >= 0; index--)
        {
            if (ids.Contains(items[index].WorkshopId))
            {
                items.RemoveAt(index);
                removed++;
            }
        }

        RecalculateCounts();
        NotifyQueueStateChanged();
        return removed;
    }

    public bool Clear()
    {
        ThrowIfDisposed();
        VerifyUiThread();
        if (IsBusy)
        {
            return false;
        }

        items.Clear();
        invalidInputs.Clear();
        logs.Clear();
        LastError = null;
        CurrentStage = null;
        CurrentWorkshopId = null;
        ProgressCompleted = 0;
        ProgressTotal = 0;
        RecalculateCounts();
        NotifyQueueStateChanged();
        return true;
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        activeCancellation?.Cancel();
        activeCancellation?.Dispose();
        activeCancellation = null;
        enqueueGate.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task EnqueueInputTextAsync()
    {
        await EnqueueAsync(InputText).ConfigureAwait(true);
    }

    private bool CanEnqueue() => !IsBusy && !string.IsNullOrWhiteSpace(InputText);

    private async Task RetryFailedCommandAsync()
    {
        await RetryFailedAsync().ConfigureAwait(true);
    }

    private async Task ResolveMetadataAsync(
        IReadOnlyCollection<DownloadQueueItemViewModel> addedItems,
        CancellationToken cancellationToken)
    {
        IReadOnlyDictionary<string, WorkshopMetadata> metadata;
        string? lookupError = null;
        try
        {
            metadata = await workshopClient.GetMetadataBatchAsync(
                addedItems.Select(item => item.WorkshopId).ToArray(),
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await InvokeOnUiAsync(() =>
            {
                foreach (var item in addedItems)
                {
                    item.CompleteMetadataLookup(
                        metadata: null,
                        "Title lookup was cancelled; the item can still be downloaded.");
                }

                NotifyQueueStateChanged();
            }).ConfigureAwait(false);
            throw;
        }
        catch (Exception exception) when (IsOperationalFailure(exception))
        {
            metadata = new Dictionary<string, WorkshopMetadata>(StringComparer.Ordinal);
            lookupError = $"Title lookup failed: {exception.Message}";
        }

        await InvokeOnUiAsync(() =>
        {
            foreach (var item in addedItems)
            {
                metadata.TryGetValue(item.WorkshopId, out var itemMetadata);
                item.CompleteMetadataLookup(
                    itemMetadata,
                    lookupError ?? (itemMetadata is null ? "Workshop title is unavailable." : null));
            }

            if (lookupError is not null)
            {
                logs.Add(lookupError);
            }

            NotifyQueueStateChanged();
        }).ConfigureAwait(false);
    }

    private void ApplyProgress(
        OperationProgress progress,
        IReadOnlyCollection<DownloadQueueItemViewModel> batch)
    {
        CurrentStage = progress.Stage;
        if (progress.WorkshopId is not null)
        {
            CurrentWorkshopId = progress.WorkshopId;
            foreach (var item in batch)
            {
                if (item.Status == DownloadQueueItemStatus.Downloading
                    && item.WorkshopId != progress.WorkshopId)
                {
                    item.MarkReady();
                }
            }

            var current = batch.FirstOrDefault(item => item.WorkshopId == progress.WorkshopId);
            current?.MarkDownloading();
        }

        if (string.Equals(progress.Stage, "DownloadingQueue", StringComparison.Ordinal))
        {
            ProgressCompleted = progress.Completed;
            ProgressTotal = progress.Total;
        }

        if (!string.IsNullOrWhiteSpace(progress.Message))
        {
            logs.Add(progress.Message);
        }
    }

    private void ApplyResults(
        IReadOnlyCollection<DownloadQueueItemViewModel> batch,
        DownloadBatchResult result)
    {
        var resultsById = result.Results.ToDictionary(
            item => item.WorkshopId,
            StringComparer.Ordinal);
        foreach (var item in batch)
        {
            if (resultsById.TryGetValue(item.WorkshopId, out var itemResult))
            {
                item.Complete(itemResult);
                if (!string.IsNullOrWhiteSpace(itemResult.Error))
                {
                    logs.Add($"{item.WorkshopId}: {itemResult.Error}");
                }
            }
            else
            {
                item.Fail("The download service did not return a result for this queue item.");
            }
        }

        CurrentStage = "Completed";
        logs.Add(
            $"Queue complete: {result.SucceededCount} succeeded, "
            + $"{result.FailedCount} failed, {result.CancelledCount} cancelled.");
    }

    private void RecalculateCounts()
    {
        SucceededCount = items.Count(item => item.Status == DownloadQueueItemStatus.Succeeded);
        FailedCount = items.Count(item => item.Status == DownloadQueueItemStatus.Failed);
        CancelledCount = items.Count(item => item.Status == DownloadQueueItemStatus.Cancelled);
    }

    private void NotifyQueueStateChanged()
    {
        OnPropertyChanged(nameof(CanStart));
        enqueueCommand.NotifyCanExecuteChanged();
        startCommand.NotifyCanExecuteChanged();
        cancelCommand.NotifyCanExecuteChanged();
        retryFailedCommand.NotifyCanExecuteChanged();
    }

    private void NotifyCommandStates()
    {
        enqueueCommand.NotifyCanExecuteChanged();
        startCommand.NotifyCanExecuteChanged();
        cancelCommand.NotifyCanExecuteChanged();
        retryFailedCommand.NotifyCanExecuteChanged();
    }

    private Task<bool> InvokeOnUiAsync(Action action) =>
        InvokeOnUiAsync(() =>
        {
            action();
            return true;
        });

    private Task<T> InvokeOnUiAsync<T>(Func<T> action)
    {
        if (Environment.CurrentManagedThreadId == uiThreadId)
        {
            return Task.FromResult(action());
        }

        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        uiContext.Post(
            _ =>
            {
                try
                {
                    completion.SetResult(action());
                }
                catch (Exception exception)
                {
                    completion.SetException(exception);
                }
            },
            state: null);
        return completion.Task;
    }

    private static async Task DrainProgressAsync(ConcurrentQueue<Task> pendingProgress)
    {
        while (!pendingProgress.IsEmpty)
        {
            var updates = new List<Task>();
            while (pendingProgress.TryDequeue(out var update))
            {
                updates.Add(update);
            }

            await Task.WhenAll(updates).ConfigureAwait(false);
        }
    }

    private static bool IsStartable(DownloadQueueItemViewModel item) =>
        item.Status is DownloadQueueItemStatus.Pending
            or DownloadQueueItemStatus.Ready
            or DownloadQueueItemStatus.Cancelled;

    private static bool IsOperationalFailure(Exception exception) =>
        exception is HttpRequestException
            or IOException
            or UnauthorizedAccessException
            or InvalidOperationException
            or ArgumentException
            or NotSupportedException;

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
    }

    private void VerifyUiThread()
    {
        if (Environment.CurrentManagedThreadId != uiThreadId)
        {
            throw new InvalidOperationException("Queue collections can only be changed from the UI thread.");
        }
    }

    private sealed class CallbackProgress<T> : IProgress<T>
    {
        private readonly Action<T> report;

        public CallbackProgress(Action<T> report)
        {
            this.report = report;
        }

        public void Report(T value) => report(value);
    }
}
