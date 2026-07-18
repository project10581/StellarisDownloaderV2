using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StellarisDownloader.Core.Models;
using StellarisDownloader.Core.Services;

namespace StellarisDownloader.App.ViewModels;

public sealed class UpdateSelectionViewModel : ObservableObject, IDisposable
{
    private const string EmptyResultMessage =
        "No mod records are available for update checks. The cache may be empty, stale, "
        + "or belong to a different library root.";
    private readonly ISettingsStore settingsStore;
    private readonly IModOperationService modOperationService;
    private readonly SynchronizationContext uiContext;
    private readonly int uiThreadId;
    private readonly ObservableCollection<UpdateCheckItemViewModel> items = [];
    private readonly AsyncRelayCommand checkUpdatesCommand;
    private readonly AsyncRelayCommand updateSelectedCommand;
    private readonly RelayCommand selectAllAvailableCommand;
    private readonly RelayCommand cancelCommand;
    private CancellationTokenSource? activeCancellation;
    private string? currentLibraryRoot;
    private bool isBusy;
    private string? currentStage;
    private int progressCompleted;
    private int progressTotal;
    private string? currentWorkshopId;
    private string? progressMessage;
    private string? statusMessage;
    private string? lastError;
    private int succeededCount;
    private int failedCount;
    private int cancelledCount;
    private bool disposed;

    public UpdateSelectionViewModel(
        ISettingsStore settingsStore,
        IModOperationService modOperationService,
        SynchronizationContext? uiContext = null)
    {
        ArgumentNullException.ThrowIfNull(settingsStore);
        ArgumentNullException.ThrowIfNull(modOperationService);

        this.settingsStore = settingsStore;
        this.modOperationService = modOperationService;
        this.uiContext = uiContext
            ?? SynchronizationContext.Current
            ?? new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher);
        uiThreadId = Environment.CurrentManagedThreadId;

        Items = new ReadOnlyObservableCollection<UpdateCheckItemViewModel>(items);
        checkUpdatesCommand = new AsyncRelayCommand(
            () => CheckUpdatesAsync(CancellationToken.None),
            () => CanCheckUpdates);
        updateSelectedCommand = new AsyncRelayCommand(
            () => UpdateSelectedAsync(CancellationToken.None),
            () => CanUpdateSelected);
        selectAllAvailableCommand = new RelayCommand(
            SelectAllAvailable,
            () => CanSelectAllAvailable);
        cancelCommand = new RelayCommand(Cancel, () => CanCancel);
    }

    public ReadOnlyObservableCollection<UpdateCheckItemViewModel> Items { get; }

    public IAsyncRelayCommand CheckUpdatesCommand => checkUpdatesCommand;

    public IAsyncRelayCommand UpdateSelectedCommand => updateSelectedCommand;

    public IRelayCommand SelectAllAvailableCommand => selectAllAvailableCommand;

    public IRelayCommand CancelCommand => cancelCommand;

    public bool IsBusy
    {
        get => isBusy;
        private set
        {
            if (SetProperty(ref isBusy, value))
            {
                OnPropertyChanged(nameof(CanCheckUpdates));
                OnPropertyChanged(nameof(CanUpdateSelected));
                OnPropertyChanged(nameof(CanSelectAllAvailable));
                OnPropertyChanged(nameof(CanCancel));
                NotifyCommandStates();
            }
        }
    }

    public bool CanCheckUpdates => !IsBusy;

    public bool CanUpdateSelected => !IsBusy
        && currentLibraryRoot is not null
        && items.Any(item => item.IsSelected && item.IsSelectable);

    public bool CanSelectAllAvailable => !IsBusy
        && items.Any(item => item.IsSelectable && !item.IsSelected);

    public bool CanCancel => IsBusy
        && activeCancellation is not null
        && !activeCancellation.IsCancellationRequested;

    public int SelectedCount => items.Count(item => item.IsSelected && item.IsSelectable);

    public string? CurrentStage
    {
        get => currentStage;
        private set => SetProperty(ref currentStage, value);
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

    public string? CurrentWorkshopId
    {
        get => currentWorkshopId;
        private set => SetProperty(ref currentWorkshopId, value);
    }

    public string? ProgressMessage
    {
        get => progressMessage;
        private set => SetProperty(ref progressMessage, value);
    }

    public string? StatusMessage
    {
        get => statusMessage;
        private set => SetProperty(ref statusMessage, value);
    }

    public string? LastError
    {
        get => lastError;
        private set => SetProperty(ref lastError, value);
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

    public async Task CheckUpdatesAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var operation = await BeginCheckAsync(cancellationToken).ConfigureAwait(false);
        if (operation is null)
        {
            return;
        }

        var pendingProgress = new ConcurrentQueue<Task>();
        var progress = CreateProgress(pendingProgress);
        try
        {
            var loaded = await settingsStore.LoadAsync(operation.Token).ConfigureAwait(false);
            if (loaded.RequiresInitialization || string.IsNullOrWhiteSpace(loaded.Settings.LibraryRoot))
            {
                await InvokeOnUiAsync(() => SetCheckFailure(
                    "Choose a mod library folder before checking for updates.")).ConfigureAwait(false);
                return;
            }

            var libraryRoot = loaded.Settings.LibraryRoot;
            var results = await modOperationService.CheckUpdatesAsync(
                libraryRoot,
                progress,
                operation.Token).ConfigureAwait(false);
            await DrainProgressAsync(pendingProgress).ConfigureAwait(false);
            await InvokeOnUiAsync(() => ApplyCheckResults(libraryRoot, results)).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await InvokeOnUiAsync(() =>
            {
                CurrentStage = "Cancelled";
                StatusMessage = "The update check was cancelled.";
            }).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsOperationalFailure(exception))
        {
            await InvokeOnUiAsync(() => SetCheckFailure(exception.Message)).ConfigureAwait(false);
        }
        finally
        {
            await DrainProgressAsync(pendingProgress).ConfigureAwait(false);
            await FinishOperationAsync(operation).ConfigureAwait(false);
        }
    }

    public void SelectAllAvailable()
    {
        ThrowIfDisposed();
        VerifyUiThread();
        if (IsBusy)
        {
            return;
        }

        foreach (var item in items.Where(item => item.IsSelectable))
        {
            item.IsSelected = true;
        }
    }

    public async Task UpdateSelectedAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var operation = await BeginUpdateAsync(cancellationToken).ConfigureAwait(false);
        if (operation is null)
        {
            return;
        }

        var pendingProgress = new ConcurrentQueue<Task>();
        var progress = CreateProgress(pendingProgress);
        try
        {
            var result = await modOperationService.UpdateSelectedAsync(
                operation.LibraryRoot,
                operation.WorkshopIds,
                progress,
                operation.Token).ConfigureAwait(false);
            await DrainProgressAsync(pendingProgress).ConfigureAwait(false);
            await InvokeOnUiAsync(() => ApplyUpdateResults(
                operation.WorkshopIds,
                result,
                operation.IsCancellationRequested)).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await InvokeOnUiAsync(() => ApplyCancelledUpdate(operation.WorkshopIds)).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsOperationalFailure(exception))
        {
            await InvokeOnUiAsync(() => ApplyFailedUpdate(
                operation.WorkshopIds,
                exception.Message)).ConfigureAwait(false);
        }
        finally
        {
            await DrainProgressAsync(pendingProgress).ConfigureAwait(false);
            await FinishOperationAsync(operation).ConfigureAwait(false);
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
            StatusMessage = "Cancellation requested.";
            cancelCommand.NotifyCanExecuteChanged();
        });
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
        foreach (var item in items)
        {
            item.PropertyChanged -= OnItemPropertyChanged;
        }

        GC.SuppressFinalize(this);
    }

    private async Task<UpdateOperation?> BeginCheckAsync(CancellationToken cancellationToken)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var started = await InvokeOnUiAsync(() =>
        {
            if (IsBusy)
            {
                return false;
            }

            activeCancellation = source;
            IsBusy = true;
            SetItemsSelectionEnabled(false);
            ReplaceItems([]);
            currentLibraryRoot = null;
            ResetOperationState("CheckingModUpdates", "Checking installed mods for updates.");
            return true;
        }).ConfigureAwait(false);
        if (!started)
        {
            source.Dispose();
            return null;
        }

        return new UpdateOperation(source, null, []);
    }

    private async Task<UpdateOperation?> BeginUpdateAsync(CancellationToken cancellationToken)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var operation = await InvokeOnUiAsync(() =>
        {
            if (IsBusy || currentLibraryRoot is null)
            {
                return null;
            }

            var workshopIds = items
                .Where(item => item.IsSelected && item.IsSelectable)
                .Select(item => item.WorkshopId)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (workshopIds.Length == 0)
            {
                return null;
            }

            activeCancellation = source;
            IsBusy = true;
            SetItemsSelectionEnabled(false);
            foreach (var item in items.Where(item => workshopIds.Contains(
                item.WorkshopId,
                StringComparer.Ordinal)))
            {
                item.ResetOperationResult();
            }

            ResetOperationState(
                "UpdatingSelectedMods",
                $"Updating {workshopIds.Length} selected mod(s).");
            ProgressTotal = workshopIds.Length;
            return new UpdateOperation(source, currentLibraryRoot, workshopIds);
        }).ConfigureAwait(false);
        if (operation is null)
        {
            source.Dispose();
        }

        return operation;
    }

    private async Task FinishOperationAsync(UpdateOperation operation)
    {
        await InvokeOnUiAsync(() =>
        {
            if (ReferenceEquals(activeCancellation, operation.Source))
            {
                activeCancellation = null;
            }

            SetItemsSelectionEnabled(true);
            IsBusy = false;
            NotifySelectionStateChanged();
        }).ConfigureAwait(false);
        operation.Dispose();
    }

    private void ApplyCheckResults(
        string libraryRoot,
        IReadOnlyList<UpdateCheckResult> results)
    {
        currentLibraryRoot = libraryRoot;
        ReplaceItems(results.Select(result => new UpdateCheckItemViewModel(result)));
        CurrentStage = "CheckCompleted";
        StatusMessage = results.Count == 0
            ? EmptyResultMessage
            : $"Checked {results.Count} installed mod(s).";
        LastError = null;
    }

    private void SetCheckFailure(string error)
    {
        currentLibraryRoot = null;
        ReplaceItems([]);
        CurrentStage = "Failed";
        StatusMessage = error;
        LastError = error;
    }

    private void ApplyUpdateResults(
        IReadOnlyCollection<string> workshopIds,
        DownloadBatchResult result,
        bool cancellationRequested)
    {
        var resultsById = new Dictionary<string, DownloadResult>(StringComparer.Ordinal);
        foreach (var itemResult in result.Results)
        {
            resultsById[itemResult.WorkshopId] = itemResult;
        }

        var succeeded = 0;
        var failed = 0;
        var cancelled = 0;
        string? firstError = null;
        foreach (var workshopId in workshopIds)
        {
            OperationStatus status;
            string? error;
            if (resultsById.TryGetValue(workshopId, out var itemResult))
            {
                status = itemResult.Status;
                error = itemResult.Error;
            }
            else
            {
                status = OperationStatus.Failed;
                error = "The update service did not return a result for this selected mod.";
            }

            foreach (var item in items.Where(item => item.WorkshopId == workshopId))
            {
                item.SetOperationResult(status, error);
            }

            switch (status)
            {
                case OperationStatus.Succeeded:
                    succeeded++;
                    break;
                case OperationStatus.Failed:
                    failed++;
                    firstError ??= error ?? $"Workshop item {workshopId} failed to update.";
                    break;
                case OperationStatus.Cancelled:
                    cancelled++;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(result));
            }
        }

        SucceededCount = succeeded;
        FailedCount = failed;
        CancelledCount = cancelled;
        LastError = firstError;
        CurrentStage = cancellationRequested && succeeded == 0 && failed == 0
            ? "Cancelled"
            : "Completed";
        StatusMessage = $"Update complete: {succeeded} succeeded, {failed} failed, "
            + $"{cancelled} cancelled.";
        NotifySelectionStateChanged();
    }

    private void ApplyCancelledUpdate(IReadOnlyCollection<string> workshopIds)
    {
        foreach (var item in items.Where(item => workshopIds.Contains(
            item.WorkshopId,
            StringComparer.Ordinal)))
        {
            item.SetOperationResult(OperationStatus.Cancelled, "The update was cancelled.");
        }

        SucceededCount = 0;
        FailedCount = 0;
        CancelledCount = workshopIds.Count;
        CurrentStage = "Cancelled";
        StatusMessage = "The selected updates were cancelled.";
        LastError = null;
    }

    private void ApplyFailedUpdate(IReadOnlyCollection<string> workshopIds, string error)
    {
        foreach (var item in items.Where(item => workshopIds.Contains(
            item.WorkshopId,
            StringComparer.Ordinal)))
        {
            item.SetOperationResult(OperationStatus.Failed, error);
        }

        SucceededCount = 0;
        FailedCount = workshopIds.Count;
        CancelledCount = 0;
        CurrentStage = "Failed";
        StatusMessage = error;
        LastError = error;
    }

    private void ApplyProgress(OperationProgress progress)
    {
        CurrentStage = progress.Stage;
        ProgressCompleted = progress.Completed;
        ProgressTotal = progress.Total;
        CurrentWorkshopId = progress.WorkshopId;
        ProgressMessage = progress.Message;
    }

    private CallbackProgress<OperationProgress> CreateProgress(
        ConcurrentQueue<Task> pendingProgress) =>
        new CallbackProgress<OperationProgress>(value =>
            pendingProgress.Enqueue(InvokeOnUiAsync(() => ApplyProgress(value))));

    private void ResetOperationState(string stage, string message)
    {
        CurrentStage = stage;
        ProgressCompleted = 0;
        ProgressTotal = 0;
        CurrentWorkshopId = null;
        ProgressMessage = null;
        StatusMessage = message;
        LastError = null;
        SucceededCount = 0;
        FailedCount = 0;
        CancelledCount = 0;
    }

    private void ReplaceItems(IEnumerable<UpdateCheckItemViewModel> replacement)
    {
        foreach (var item in items)
        {
            item.PropertyChanged -= OnItemPropertyChanged;
        }

        items.Clear();
        foreach (var item in replacement)
        {
            item.PropertyChanged += OnItemPropertyChanged;
            items.Add(item);
        }

        NotifySelectionStateChanged();
    }

    private void SetItemsSelectionEnabled(bool value)
    {
        foreach (var item in items)
        {
            item.SetSelectionEnabled(value);
        }
    }

    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(UpdateCheckItemViewModel.IsSelected))
        {
            NotifySelectionStateChanged();
        }
    }

    private void NotifySelectionStateChanged()
    {
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(CanUpdateSelected));
        OnPropertyChanged(nameof(CanSelectAllAvailable));
        updateSelectedCommand.NotifyCanExecuteChanged();
        selectAllAvailableCommand.NotifyCanExecuteChanged();
    }

    private void NotifyCommandStates()
    {
        checkUpdatesCommand.NotifyCanExecuteChanged();
        updateSelectedCommand.NotifyCanExecuteChanged();
        selectAllAvailableCommand.NotifyCanExecuteChanged();
        cancelCommand.NotifyCanExecuteChanged();
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
            null);
        return completion.Task;
    }

    private static async Task DrainProgressAsync(ConcurrentQueue<Task> pendingProgress)
    {
        while (!pendingProgress.IsEmpty)
        {
            var pending = new List<Task>();
            while (pendingProgress.TryDequeue(out var update))
            {
                pending.Add(update);
            }

            if (pending.Count > 0)
            {
                await Task.WhenAll(pending).ConfigureAwait(false);
            }
        }
    }

    private void VerifyUiThread()
    {
        if (Environment.CurrentManagedThreadId != uiThreadId)
        {
            throw new InvalidOperationException("This operation must run on the UI thread.");
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(disposed, this);

    private static bool IsOperationalFailure(Exception exception) =>
        exception is IOException
            or UnauthorizedAccessException
            or InvalidOperationException
            or InvalidDataException
            or ArgumentException
            or NotSupportedException
            or HttpRequestException;

    private sealed class CallbackProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }

    private sealed class UpdateOperation(
        CancellationTokenSource source,
        string? libraryRoot,
        IReadOnlyCollection<string> workshopIds) : IDisposable
    {
        public CancellationTokenSource Source { get; } = source;

        public CancellationToken Token => Source.Token;

        public string LibraryRoot { get; } = libraryRoot ?? string.Empty;

        public IReadOnlyCollection<string> WorkshopIds { get; } = workshopIds;

        public bool IsCancellationRequested => Source.IsCancellationRequested;

        public void Dispose() => Source.Dispose();
    }
}
