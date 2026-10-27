using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StellarisDownloader.Core.Models;
using StellarisDownloader.Core.Services;

namespace StellarisDownloader.App.ViewModels;

public sealed partial class UpdateSelectionViewModel : ObservableObject, IDisposable
{
    private const string EmptyResultMessage =
        "No mod records are available for update checks. The cache may be empty, stale, "
        + "or belong to a different library root.";
    private readonly ISettingsStore settingsStore;
    private readonly IModOperationService modOperationService;
    private readonly ObservableCollection<UpdateCheckItemViewModel> items = [];
    private CancellationTokenSource? activeCancellation;
    private string? currentLibraryRoot;
    private bool disposed;

    public UpdateSelectionViewModel(ISettingsStore settingsStore, IModOperationService modOperationService)
    {
        ArgumentNullException.ThrowIfNull(settingsStore);
        ArgumentNullException.ThrowIfNull(modOperationService);
        this.settingsStore = settingsStore;
        this.modOperationService = modOperationService;
        Items = new ReadOnlyObservableCollection<UpdateCheckItemViewModel>(items);
    }

    public ReadOnlyObservableCollection<UpdateCheckItemViewModel> Items { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCheckUpdates), nameof(CanUpdateSelected),
        nameof(CanSelectAllAvailable), nameof(CanCancel))]
    [NotifyCanExecuteChangedFor(nameof(CheckUpdatesCommand), nameof(UpdateSelectedCommand),
        nameof(SelectAllAvailableCommand), nameof(CancelCommand))]
    public partial bool IsBusy { get; private set; }

    public bool CanCheckUpdates => !IsBusy;
    public bool CanUpdateSelected => !IsBusy && currentLibraryRoot is not null
        && items.Any(item => item.IsSelected && item.IsSelectable);
    public bool CanSelectAllAvailable => !IsBusy && items.Any(item => item.IsSelectable && !item.IsSelected);
    public bool CanCancel => IsBusy && activeCancellation is { IsCancellationRequested: false };
    public int SelectedCount => items.Count(item => item.IsSelected && item.IsSelectable);

    [ObservableProperty]
    public partial string? CurrentStage { get; private set; }

    [ObservableProperty]
    public partial int ProgressCompleted { get; private set; }

    [ObservableProperty]
    public partial int ProgressTotal { get; private set; }

    [ObservableProperty]
    public partial string? CurrentWorkshopId { get; private set; }

    [ObservableProperty]
    public partial string? ProgressMessage { get; private set; }

    [ObservableProperty]
    public partial string? StatusMessage { get; private set; }

    [ObservableProperty]
    public partial string? LastError { get; private set; }

    [ObservableProperty]
    public partial int SucceededCount { get; private set; }

    [ObservableProperty]
    public partial int FailedCount { get; private set; }

    [ObservableProperty]
    public partial int CancelledCount { get; private set; }

    [RelayCommand(CanExecute = nameof(CanCheckUpdates), AllowConcurrentExecutions = false)]
    public async Task CheckUpdatesAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var operation = BeginCheck(cancellationToken);
        if (operation is null)
        {
            return;
        }

        var progress = new Progress<OperationProgress>(ApplyProgress);
        try
        {
            var loaded = await settingsStore.LoadAsync(operation.Token);
            if (loaded.RequiresInitialization || string.IsNullOrWhiteSpace(loaded.Settings.LibraryRoot))
            {
                SetCheckFailure(
                    "Choose a mod library folder before checking for updates.");
                return;
            }

            var libraryRoot = loaded.Settings.LibraryRoot;
            var results = await modOperationService.CheckUpdatesAsync(
                libraryRoot,
                progress,
                operation.Token);
            await Task.Yield();
            ApplyCheckResults(libraryRoot, results);
        }
        catch (OperationCanceledException exception) when (!OperationalFailure.IsTimeout(exception, cancellationToken))
        {
            await Task.Yield();
            CurrentStage = "Cancelled";
            StatusMessage = "The update check was cancelled.";
        }
        catch (Exception exception) when (OperationalFailure.IsRecoverable(exception, cancellationToken))
        {
            await Task.Yield();
            SetCheckFailure(exception.Message);
        }
        finally
        {
            FinishOperation(operation);
        }
    }

    [RelayCommand(CanExecute = nameof(CanSelectAllAvailable))]
    public void SelectAllAvailable()
    {
        ThrowIfDisposed();
        if (IsBusy)
        {
            return;
        }

        foreach (var item in items.Where(item => item.IsSelectable))
        {
            item.IsSelected = true;
        }
    }

    [RelayCommand(CanExecute = nameof(CanUpdateSelected), AllowConcurrentExecutions = false)]
    public async Task UpdateSelectedAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var operation = BeginUpdate(cancellationToken);
        if (operation is null)
        {
            return;
        }

        var progress = new Progress<OperationProgress>(ApplyProgress);
        try
        {
            var result = await modOperationService.UpdateSelectedAsync(
                operation.LibraryRoot,
                operation.WorkshopIds,
                progress,
                operation.Token);
            await Task.Yield();
            ApplyUpdateResults(
                operation.WorkshopIds,
                result,
                operation.IsCancellationRequested);
        }
        catch (OperationCanceledException exception) when (!OperationalFailure.IsTimeout(exception, cancellationToken))
        {
            await Task.Yield();
            ApplyCancelledUpdate(operation.WorkshopIds);
        }
        catch (Exception exception) when (OperationalFailure.IsRecoverable(exception, cancellationToken))
        {
            await Task.Yield();
            ApplyFailedUpdate(
                operation.WorkshopIds,
                exception.Message);
        }
        finally
        {
            FinishOperation(operation);
        }
    }

    [RelayCommand(CanExecute = nameof(CanCancel))]
    public void Cancel()
    {
        ThrowIfDisposed();
        var cancellation = activeCancellation;
        if (cancellation is null || cancellation.IsCancellationRequested)
        {
            return;
        }

        cancellation.Cancel();
        StatusMessage = "Cancellation requested.";
        OnPropertyChanged(nameof(CanCancel));
        CancelCommand.NotifyCanExecuteChanged();
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

    private UpdateOperation? BeginCheck(CancellationToken cancellationToken)
    {
        if (IsBusy) { return null; }
        var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        activeCancellation = source;
        IsBusy = true;
        SetItemsSelectionEnabled(false);
        ReplaceItems([]);
        currentLibraryRoot = null;
        ResetOperationState("CheckingModUpdates", "Checking installed mods for updates.");
        return new UpdateOperation(source, null, []);
    }

    private UpdateOperation? BeginUpdate(CancellationToken cancellationToken)
    {
        if (IsBusy || currentLibraryRoot is null) { return null; }
        var workshopIds = items.Where(item => item.IsSelected && item.IsSelectable)
            .Select(item => item.WorkshopId).Distinct(StringComparer.Ordinal).ToArray();
        if (workshopIds.Length == 0) { return null; }
        var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        activeCancellation = source;
        IsBusy = true;
        SetItemsSelectionEnabled(false);
        foreach (var item in items.Where(item => workshopIds.Contains(item.WorkshopId, StringComparer.Ordinal)))
        {
            item.ResetOperationResult();
        }

        ResetOperationState("UpdatingSelectedMods", $"Updating {workshopIds.Length} selected mod(s).");
        ProgressTotal = workshopIds.Length;
        return new UpdateOperation(source, currentLibraryRoot, workshopIds);
    }

    private void FinishOperation(UpdateOperation operation)
    {
        activeCancellation = null;
        SetItemsSelectionEnabled(true);
        IsBusy = false;
        NotifySelectionStateChanged();
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
        UpdateSelectedCommand.NotifyCanExecuteChanged();
        SelectAllAvailableCommand.NotifyCanExecuteChanged();
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(disposed, this);

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
