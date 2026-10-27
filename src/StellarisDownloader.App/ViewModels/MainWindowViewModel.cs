using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Data.Sqlite;
using StellarisDownloader.App.Services;
using StellarisDownloader.Core.Models;
using StellarisDownloader.Core.Services;

namespace StellarisDownloader.App.ViewModels;

public sealed partial class MainWindowViewModel : ObservableObject, IDisposable
{
    private readonly ISettingsStore settingsStore;
    private readonly IModRepository modRepository;
    private readonly ILibraryService libraryService;
    private readonly IPreviewImageService previewImageService;
    private readonly IModOperationService modOperationService;
    private readonly DownloadQueueViewModel downloadQueue;
    private readonly ObservableCollection<ModListItemViewModel> items = [];
    private readonly IDialogService dialogs;
    private readonly Func<string, string?> text;
    private CancellationTokenSource? previewCancellation;
    private string? libraryRoot;
    private bool refreshLibraryOnStartup;

    public MainWindowViewModel(
        ISettingsStore settingsStore,
        IModRepository modRepository,
        ILibraryService libraryService,
        IPreviewImageService previewImageService,
        IModOperationService modOperationService,
        DownloadQueueViewModel downloadQueue,
        IDialogService? dialogs = null,
        Func<string, string?>? text = null)
    {
        ArgumentNullException.ThrowIfNull(settingsStore);
        ArgumentNullException.ThrowIfNull(modRepository);
        ArgumentNullException.ThrowIfNull(libraryService);
        ArgumentNullException.ThrowIfNull(previewImageService);
        ArgumentNullException.ThrowIfNull(modOperationService);
        ArgumentNullException.ThrowIfNull(downloadQueue);

        this.settingsStore = settingsStore;
        this.modRepository = modRepository;
        this.libraryService = libraryService;
        this.previewImageService = previewImageService;
        this.modOperationService = modOperationService;
        this.downloadQueue = downloadQueue;
        this.dialogs = dialogs ?? new DialogService();
        this.text = text ?? (key => Application.Current?.TryFindResource(key) as string);
        downloadQueue.PropertyChanged += OnDownloadQueuePropertyChanged;
        downloadQueue.BatchCompleted += OnDownloadBatchCompletedAsync;

        ModsView = new ListCollectionView(items)
        {
            Filter = FilterMod,
        };
        ApplySort();
    }

    public ICollectionView ModsView { get; }

    public event Action? DownloadQueueRequested;

    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial ModSortOption SelectedSort { get; set; } = ModSortOption.Title;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection), nameof(CanModifySelectedMod))]
    [NotifyCanExecuteChangedFor(nameof(DeleteSelectedCommand), nameof(RedownloadSelectedCommand))]
    public partial ModListItemViewModel? SelectedMod { get; set; }

    [ObservableProperty]
    public partial ImageSource? PreviewImage { get; private set; }

    [ObservableProperty]
    public partial LibraryViewState State { get; private set; } = LibraryViewState.Loading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRunWriteOperations), nameof(CanManageSnapshots), nameof(CanModifySelectedMod))]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand), nameof(RescanCommand), nameof(DeleteSelectedCommand), nameof(RedownloadSelectedCommand))]
    public partial bool IsBusy { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRunWriteOperations), nameof(CanModifySelectedMod))]
    [NotifyCanExecuteChangedFor(nameof(RescanCommand), nameof(DeleteSelectedCommand), nameof(RedownloadSelectedCommand))]
    public partial bool CanModifyLibrary { get; private set; }

    partial void OnSearchTextChanged(string value)
    {
        if (value is null) { SearchText = string.Empty; }
        else { ModsView.Refresh(); }
    }

    partial void OnSelectedSortChanged(ModSortOption value) => ApplySort();

    partial void OnSelectedModChanged(ModListItemViewModel? value) => StartPreviewLoad(value);

    public bool HasSelection => SelectedMod is not null;

    public bool CanRunWriteOperations => CanModifyLibrary && !IsBusy && !downloadQueue.IsBusy;

    public bool CanManageSnapshots => !IsBusy && !downloadQueue.IsBusy;

    public bool CanModifySelectedMod => CanRunWriteOperations && HasSelection;

    [ObservableProperty]
    public partial string? ProgressMessage { get; private set; }

    [ObservableProperty]
    public partial string? LastError { get; private set; }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await RefreshAsync(cancellationToken);
        if (refreshLibraryOnStartup
            && libraryRoot is not null
            && State is LibraryViewState.Ready or LibraryViewState.Stale)
        {
            await RescanAsync(cancellationToken);
        }
    }

    private bool CanRefresh => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanRefresh), AllowConcurrentExecutions = false)]
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            await RefreshLibraryStateAsync(cancellationToken);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanModifySelectedMod), AllowConcurrentExecutions = false)]
    private Task DeleteSelected() => ConfirmAndDeleteAsync(queueForRedownload: false);

    [RelayCommand(CanExecute = nameof(CanModifySelectedMod), AllowConcurrentExecutions = false)]
    private Task RedownloadSelected() => ConfirmAndDeleteAsync(queueForRedownload: true);

    private async Task ConfirmAndDeleteAsync(bool queueForRedownload)
    {
        var selected = SelectedMod;
        if (selected is null || !CanModifySelectedMod) { return; }
        var message = FormatResource(queueForRedownload ? "Delete.RedownloadConfirmMessage" : "Delete.ConfirmMessage",
            selected.DisplayTitle, selected.WorkshopId);
        if (!dialogs.Confirm(message, text("Delete.ConfirmTitle") ?? "Confirm mod deletion", MessageBoxImage.Warning)) { return; }

        var result = await DeleteSelectedCoreAsync(false, queueForRedownload, null, CancellationToken.None);
        if (result.CanRetryPermanently && dialogs.Confirm(
            FormatResource("Delete.PermanentConfirmMessage", selected.DisplayTitle, selected.WorkshopId),
            text("Delete.ConfirmTitle") ?? "Confirm permanent deletion", MessageBoxImage.Stop))
        {
            result = await DeleteSelectedCoreAsync(true, queueForRedownload, null, CancellationToken.None);
        }

        if (result.QueuedForRedownload) { DownloadQueueRequested?.Invoke(); }
        if (!string.IsNullOrWhiteSpace(result.Error))
        {
            var error = result.RequiresRescan ? text("Delete.RescanRequired") ?? result.Error : result.Error;
            dialogs.ShowError(error, text("Delete.ErrorTitle") ?? "Mod operation failed",
                result.RequiresRescan ? MessageBoxImage.Warning : MessageBoxImage.Error);
        }
    }

    private string FormatResource(string key, params object[] arguments) =>
        string.Format(CultureInfo.CurrentCulture, text(key) ?? key, arguments);

    public Task<SelectedModDeletionResult> DeleteSelectedAsync(
        bool permanently,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        DeleteSelectedCoreAsync(
            permanently,
            queueForRedownload: false,
            progress,
            cancellationToken);

    public Task<SelectedModDeletionResult> DeleteAndQueueRedownloadAsync(
        bool permanently,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        DeleteSelectedCoreAsync(
            permanently,
            queueForRedownload: true,
            progress,
            cancellationToken);

    [RelayCommand(CanExecute = nameof(CanRescan), AllowConcurrentExecutions = false)]
    public async Task RescanAsync(CancellationToken cancellationToken = default)
    {
        if (!CanRescan() || libraryRoot is null)
        {
            return;
        }

        IsBusy = true;
        State = LibraryViewState.Loading;
        ProgressMessage = null;
        LastError = null;
        var progress = CreateOperationProgress();
        try
        {
            var junction = await libraryService.EnsureJunctionAsync(
                libraryRoot,
                progress,
                cancellationToken);
            if (junction.Status != OperationStatus.Succeeded)
            {
                ClearItems();
                CanModifyLibrary = false;
                LastError = junction.Error;
                State = LibraryViewState.Error;
                return;
            }

            var result = await libraryService.ScanAsync(
                libraryRoot,
                progress,
                cancellationToken);
            if (result.Status == OperationStatus.Succeeded)
            {
                ReplaceItems(result.Records);
                CanModifyLibrary = true;
                State = LibraryViewState.Ready;
            }
            else
            {
                ClearItems();
                CanModifyLibrary = false;
                LastError = result.Error;
                State = LibraryViewState.Stale;
            }
        }
        finally
        {
            await Task.Yield();
            ProgressMessage = null;
            IsBusy = false;
        }
    }

    public void Dispose()
    {
        downloadQueue.PropertyChanged -= OnDownloadQueuePropertyChanged;
        downloadQueue.BatchCompleted -= OnDownloadBatchCompletedAsync;
        previewCancellation?.Cancel();
        previewCancellation?.Dispose();
        GC.SuppressFinalize(this);
    }

    private bool CanRescan() => !IsBusy && !downloadQueue.IsBusy && libraryRoot is not null;

    private async Task RefreshLibraryStateAsync(CancellationToken cancellationToken)
    {
        State = LibraryViewState.Loading;
        ProgressMessage = null;
        LastError = null;
        var progress = CreateOperationProgress();
        try
        {
            var settings = await settingsStore.LoadAsync(cancellationToken);
            libraryRoot = settings.Settings.LibraryRoot;
            refreshLibraryOnStartup = settings.Settings.RefreshLibraryOnStartup;
            if (settings.RequiresInitialization || string.IsNullOrWhiteSpace(libraryRoot))
            {
                ClearItems();
                CanModifyLibrary = false;
                State = LibraryViewState.NoLibrary;
                return;
            }

            var junction = await libraryService.EnsureJunctionAsync(
                libraryRoot,
                progress,
                cancellationToken);
            if (junction.Status != OperationStatus.Succeeded)
            {
                ClearItems();
                CanModifyLibrary = false;
                LastError = junction.Error;
                State = LibraryViewState.Error;
                return;
            }

            var cacheState = await modRepository.GetCacheStateAsync(libraryRoot, cancellationToken);
            if (cacheState.State != CacheState.Valid)
            {
                ClearItems();
                CanModifyLibrary = false;
                State = LibraryViewState.Stale;
                return;
            }

            var records = await modRepository.ListAsync(libraryRoot, cancellationToken);
            ReplaceItems(records);
            CanModifyLibrary = true;
            State = LibraryViewState.Ready;
        }
        catch (OperationCanceledException exception) when (!OperationalFailure.IsTimeout(exception, cancellationToken))
        {
            throw;
        }
        catch (Exception exception) when (OperationalFailure.IsRecoverable(exception, cancellationToken))
        {
            ClearItems();
            CanModifyLibrary = false;
            LastError = exception.Message;
            State = LibraryViewState.Error;
        }
        finally
        {
            await Task.Yield();
            ProgressMessage = null;
        }
    }

    private async Task<SelectedModDeletionResult> DeleteSelectedCoreAsync(
        bool permanently,
        bool queueForRedownload,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (!CanModifySelectedMod || libraryRoot is null || SelectedMod is null)
        {
            return SelectedModDeletionResult.Rejected(
                "The selected mod cannot be changed while the library or download queue is busy, "
                + "or while the library cache is unavailable.");
        }

        var workshopId = SelectedMod.WorkshopId;
        var currentLibraryRoot = libraryRoot;
        IsBusy = true;
        LastError = null;
        try
        {
            var deleteResult = await modOperationService.DeleteAsync(
                currentLibraryRoot,
                workshopId,
                permanently,
                progress,
                cancellationToken);

            if (deleteResult.RequiresRescan)
            {
                await modRepository.MarkCacheStaleAsync(CancellationToken.None);
                ClearItems();
                CanModifyLibrary = false;
                State = LibraryViewState.Stale;
                LastError = BuildRescanRequiredError(deleteResult.Error);
            }
            else if (deleteResult.Status == OperationStatus.Succeeded || deleteResult.FilesRemoved)
            {
                await RefreshLibraryStateAsync(cancellationToken);
            }
            else
            {
                LastError = deleteResult.Error;
            }

            var queued = false;
            string? actionError = deleteResult.Error;
            if (queueForRedownload && deleteResult.Status == OperationStatus.Succeeded)
            {
                if (downloadQueue.IsBusy)
                {
                    actionError = "The mod was deleted, but the download queue started running "
                        + "before it could be queued again.";
                }
                else
                {
                    downloadQueue.Remove([workshopId]);
                    var enqueueResult = await downloadQueue.EnqueueAsync(workshopId, cancellationToken);
                    queued = enqueueResult.AddedCount == 1;
                    if (!queued)
                    {
                        actionError = "The mod was deleted, but it could not be added to the download queue.";
                    }
                }

                if (actionError is not null)
                {
                    LastError = actionError;
                }
            }

            return new SelectedModDeletionResult
            {
                Started = true,
                DeleteResult = deleteResult,
                QueuedForRedownload = queued,
                Error = actionError,
            };
        }
        catch (OperationCanceledException exception) when (!OperationalFailure.IsTimeout(exception, cancellationToken))
        {
            throw;
        }
        catch (Exception exception) when (OperationalFailure.IsRecoverable(exception, cancellationToken))
        {
            LastError = exception.Message;
            return new SelectedModDeletionResult
            {
                Started = true,
                Error = exception.Message,
            };
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void OnDownloadQueuePropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(DownloadQueueViewModel.IsBusy))
        {
            OnPropertyChanged(nameof(CanRunWriteOperations));
            OnPropertyChanged(nameof(CanManageSnapshots));
            OnPropertyChanged(nameof(CanModifySelectedMod));
            RescanCommand.NotifyCanExecuteChanged();
            DeleteSelectedCommand.NotifyCanExecuteChanged();
            RedownloadSelectedCommand.NotifyCanExecuteChanged();
        }
    }

    private async Task OnDownloadBatchCompletedAsync()
    {
        if (!IsBusy)
        {
            await RefreshAsync();
        }
    }

    private Progress<OperationProgress> CreateOperationProgress() =>
        new(UpdateOperationProgress);

    private void UpdateOperationProgress(OperationProgress value)
    {
        if (string.IsNullOrWhiteSpace(value.Message))
        {
            return;
        }

        ProgressMessage = value.Total > 0
            ? $"[{value.Completed}/{value.Total}] {value.Message}"
            : value.Message;
    }

    private static string BuildRescanRequiredError(string? error) =>
        $"{error ?? "The cache could not be updated after deleting the mod."} "
        + "Rescan the mod library before performing another write operation.";

    private bool FilterMod(object candidate)
    {
        if (candidate is not ModListItemViewModel mod || string.IsNullOrWhiteSpace(SearchText))
        {
            return true;
        }

        return mod.DisplayTitle.Contains(SearchText, StringComparison.OrdinalIgnoreCase)
            || mod.WorkshopId.Contains(SearchText, StringComparison.OrdinalIgnoreCase);
    }

    private void ApplySort()
    {
        using var deferred = ModsView.DeferRefresh();
        ModsView.SortDescriptions.Clear();
        var (propertyName, direction) = SelectedSort switch
        {
            ModSortOption.Title => (nameof(ModListItemViewModel.DisplayTitle), ListSortDirection.Ascending),
            ModSortOption.RemoteUpdated =>
                (nameof(ModListItemViewModel.RemoteUpdatedAtUtc), ListSortDirection.Descending),
            ModSortOption.LastDownloaded =>
                (nameof(ModListItemViewModel.LastDownloadedAtUtc), ListSortDirection.Descending),
            ModSortOption.FileSize =>
                (nameof(ModListItemViewModel.FileSize), ListSortDirection.Descending),
            _ => throw new InvalidOperationException($"Unsupported sort option: {SelectedSort}"),
        };
        ModsView.SortDescriptions.Add(new SortDescription(propertyName, direction));
    }

    private void ReplaceItems(IEnumerable<ModRecord> records)
    {
        var selectedId = SelectedMod?.WorkshopId;
        items.Clear();
        foreach (var record in records)
        {
            items.Add(new ModListItemViewModel(record));
        }

        SelectedMod = selectedId is null
            ? null
            : items.FirstOrDefault(item => item.WorkshopId == selectedId);
    }

    private void ClearItems()
    {
        SelectedMod = null;
        items.Clear();
    }

    private void StartPreviewLoad(ModListItemViewModel? item)
    {
        previewCancellation?.Cancel();
        previewCancellation?.Dispose();
        previewCancellation = null;
        PreviewImage = null;

        if (item is null || string.IsNullOrWhiteSpace(item.Record.PreviewUrl))
        {
            return;
        }

        var cancellation = new CancellationTokenSource();
        previewCancellation = cancellation;
        _ = LoadPreviewAsync(item, cancellation.Token);
    }

    private async Task LoadPreviewAsync(
        ModListItemViewModel item,
        CancellationToken cancellationToken)
    {
        try
        {
            var image = await previewImageService.LoadAsync(
                item.Record.PreviewUrl,
                cancellationToken);
            if (!cancellationToken.IsCancellationRequested && ReferenceEquals(item, SelectedMod))
            {
                PreviewImage = image;
            }
        }
        catch (OperationCanceledException exception) when (!OperationalFailure.IsTimeout(exception, cancellationToken))
        {
            // A newer selection owns the preview area.
        }
    }

}
