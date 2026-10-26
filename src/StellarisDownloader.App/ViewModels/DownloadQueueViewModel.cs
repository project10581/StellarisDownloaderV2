using System.Collections;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StellarisDownloader.Core.Models;
using StellarisDownloader.Core.Services;

namespace StellarisDownloader.App.ViewModels;

public sealed partial class DownloadQueueViewModel : ObservableObject, IDisposable
{
    private readonly ISettingsStore settingsStore;
    private readonly IWorkshopClient workshopClient;
    private readonly IModOperationService modOperationService;
    private readonly SemaphoreSlim enqueueGate = new(1, 1);
    private readonly ObservableCollection<DownloadQueueItemViewModel> items = [];
    private readonly ObservableCollection<InvalidWorkshopInput> invalidInputs = [];
    private readonly ObservableCollection<string> logs = [];
    private CancellationTokenSource? activeCancellation;
    private bool disposed;

    internal event Func<Task>? BatchCompleted;

    public DownloadQueueViewModel(
        ISettingsStore settingsStore,
        IWorkshopClient workshopClient,
        IModOperationService modOperationService)
    {
        ArgumentNullException.ThrowIfNull(settingsStore);
        ArgumentNullException.ThrowIfNull(workshopClient);
        ArgumentNullException.ThrowIfNull(modOperationService);
        this.settingsStore = settingsStore;
        this.workshopClient = workshopClient;
        this.modOperationService = modOperationService;
        Items = new ReadOnlyObservableCollection<DownloadQueueItemViewModel>(items);
        InvalidInputs = new ReadOnlyObservableCollection<InvalidWorkshopInput>(invalidInputs);
        Logs = new ReadOnlyObservableCollection<string>(logs);
    }

    public ReadOnlyObservableCollection<DownloadQueueItemViewModel> Items { get; }
    public ReadOnlyObservableCollection<InvalidWorkshopInput> InvalidInputs { get; }
    public ReadOnlyObservableCollection<string> Logs { get; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(EnqueueCommand))]
    public partial string InputText { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStart), nameof(IsIdle), nameof(CanCancel))]
    [NotifyCanExecuteChangedFor(nameof(EnqueueCommand), nameof(StartCommand), nameof(CancelCommand),
        nameof(RetryFailedCommand), nameof(RemoveSelectedCommand), nameof(ClearQueueCommand))]
    public partial bool IsBusy { get; private set; }

    [ObservableProperty]
    public partial string? CurrentStage { get; private set; }

    [ObservableProperty]
    public partial string? CurrentWorkshopId { get; private set; }

    [ObservableProperty]
    public partial int ProgressCompleted { get; private set; }

    [ObservableProperty]
    public partial int ProgressTotal { get; private set; }

    [ObservableProperty]
    public partial int SucceededCount { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RetryFailedCommand))]
    public partial int FailedCount { get; private set; }

    [ObservableProperty]
    public partial int CancelledCount { get; private set; }

    [ObservableProperty]
    public partial string? LastError { get; private set; }

    public bool CanStart => !IsBusy && items.Any(IsStartable);
    public bool IsIdle => !IsBusy;
    public bool CanCancel => IsBusy && activeCancellation is { IsCancellationRequested: false };

    partial void OnInputTextChanged(string value)
    {
        if (value is null) { InputText = string.Empty; }
    }

    public async Task<DownloadQueueEnqueueResult> EnqueueAsync(
        string? input, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var parsed = WorkshopIdParser.Parse(input);
        await enqueueGate.WaitAsync(cancellationToken);
        try
        {
            invalidInputs.Clear();
            foreach (var invalidInput in parsed.InvalidInputs) { invalidInputs.Add(invalidInput); }
            var existingIds = items.Select(item => item.WorkshopId).ToHashSet(StringComparer.Ordinal);
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
            if (addedItems.Count > 0) { await ResolveMetadataAsync(addedItems, cancellationToken); }
            return new DownloadQueueEnqueueResult(
                addedItems.Select(item => item.WorkshopId).ToArray(), duplicates, parsed.InvalidInputs);
        }
        finally
        {
            enqueueGate.Release();
        }
    }

    [RelayCommand(CanExecute = nameof(CanStart), AllowConcurrentExecutions = false)]
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (IsBusy) { return; }
        var batch = items.Where(IsStartable).ToArray();
        if (batch.Length == 0) { return; }
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        activeCancellation = cancellation;
        IsBusy = true;
        LastError = null;
        CurrentStage = "PreparingDownloadQueue";
        CurrentWorkshopId = null;
        ProgressCompleted = 0;
        ProgressTotal = batch.Length;
        foreach (var item in batch) { item.MarkReady(); }
        logs.Add($"Starting download queue with {batch.Length} item(s).");
        NotifyQueueStateChanged();
        var progress = new Progress<OperationProgress>(value => ApplyProgress(value, batch));
        DownloadBatchResult? batchResult = null;
        string? batchFailure = null;
        var wasCancelled = false;
        try
        {
            var settings = await settingsStore.LoadAsync(cancellation.Token);
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
                batchResult = await modOperationService.DownloadBatchAsync(requests, progress, cancellation.Token);
            }
        }
        catch (OperationCanceledException exception) when (!OperationalFailure.IsTimeout(exception, cancellation.Token))
        {
            wasCancelled = true;
        }
        catch (Exception exception) when (OperationalFailure.IsRecoverable(exception, cancellation.Token))
        {
            batchFailure = exception.Message;
        }
        finally
        {
            // Apply progress already posted to the UI context before setting the final queue state.
            await Task.Yield();
            if (batchResult is not null)
            {
                ApplyResults(batch, batchResult);
            }
            else if (wasCancelled || cancellation.IsCancellationRequested)
            {
                const string error = "Download queue was cancelled.";
                foreach (var item in batch.Where(item => !item.IsTerminal)) { item.Cancel(error); }
                logs.Add(error);
                CurrentStage = "Cancelled";
            }
            else
            {
                var error = batchFailure ?? "The download queue failed.";
                foreach (var item in batch.Where(item => !item.IsTerminal)) { item.Fail(error); }
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
        }

        if (!disposed && batchResult is not null && BatchCompleted is { } batchCompleted)
        {
            foreach (var handler in batchCompleted.GetInvocationList().Cast<Func<Task>>()) { await handler(); }
        }
    }

    [RelayCommand(CanExecute = nameof(CanCancel))]
    public void Cancel()
    {
        ThrowIfDisposed();
        if (activeCancellation is not { IsCancellationRequested: false }) { return; }
        activeCancellation.Cancel();
        logs.Add("Cancellation requested.");
        OnPropertyChanged(nameof(CanCancel));
        CancelCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanRetryFailed), AllowConcurrentExecutions = false)]
    public Task<int> RetryFailedAsync()
    {
        ThrowIfDisposed();
        if (IsBusy) { return Task.FromResult(0); }
        var failed = items.Where(item => item.Status == DownloadQueueItemStatus.Failed).ToArray();
        foreach (var item in failed) { item.MarkReady(); }
        if (failed.Length > 0) { logs.Add($"Returned {failed.Length} failed item(s) to the download queue."); }
        RecalculateCounts();
        NotifyQueueStateChanged();
        return Task.FromResult(failed.Length);
    }

    private bool CanRetryFailed() => !IsBusy && FailedCount > 0;

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private void RemoveSelected(IList selectedItems) =>
        Remove(selectedItems.OfType<DownloadQueueItemViewModel>().Select(item => item.WorkshopId));

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private void ClearQueue() => Clear();

    public int Remove(IEnumerable<string> workshopIds)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(workshopIds);
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

    [RelayCommand(CanExecute = nameof(CanEnqueue), AllowConcurrentExecutions = false)]
    private async Task Enqueue()
    {
        await EnqueueAsync(InputText);
    }

    private bool CanEnqueue() => !IsBusy && !string.IsNullOrWhiteSpace(InputText);

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
                cancellationToken: cancellationToken);
        }
        catch (OperationCanceledException exception) when (!OperationalFailure.IsTimeout(exception, cancellationToken))
        {
            foreach (var item in addedItems)
            {
                item.CompleteMetadataLookup(
                    metadata: null,
                    "Title lookup was cancelled; the item can still be downloaded.");
            }

            NotifyQueueStateChanged();
            throw;
        }
        catch (Exception exception) when (OperationalFailure.IsRecoverable(exception, cancellationToken))
        {
            metadata = new Dictionary<string, WorkshopMetadata>(StringComparer.Ordinal);
            lookupError = $"Title lookup failed: {exception.Message}";
        }

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
    }

    private void ApplyProgress(
        OperationProgress progress,
        IReadOnlyCollection<DownloadQueueItemViewModel> batch)
    {
        CurrentStage = progress.Stage;
        if (progress.ItemResult is { } result)
        {
            batch.FirstOrDefault(item => item.WorkshopId == result.WorkshopId)?.Complete(result);
            CurrentWorkshopId = null;
            ProgressCompleted = batch.Count(item => item.IsTerminal);
            RecalculateCounts();
        }
        else if (progress.WorkshopId is not null)
        {
            var current = batch.FirstOrDefault(item => item.WorkshopId == progress.WorkshopId);
            if (current is { IsTerminal: false })
            {
                CurrentWorkshopId = progress.WorkshopId;
                current.MarkDownloading();
            }
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
        EnqueueCommand.NotifyCanExecuteChanged();
        StartCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
        RetryFailedCommand.NotifyCanExecuteChanged();
    }

    private static bool IsStartable(DownloadQueueItemViewModel item) =>
        item.Status is DownloadQueueItemStatus.Pending
            or DownloadQueueItemStatus.Ready
            or DownloadQueueItemStatus.Cancelled;

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
    }

}
