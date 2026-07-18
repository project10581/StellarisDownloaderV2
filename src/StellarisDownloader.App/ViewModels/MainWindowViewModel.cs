using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows.Data;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Data.Sqlite;
using StellarisDownloader.App.Services;
using StellarisDownloader.Core.Models;
using StellarisDownloader.Core.Services;

namespace StellarisDownloader.App.ViewModels;

public sealed class MainWindowViewModel : ObservableObject, IDisposable
{
    private readonly ISettingsStore settingsStore;
    private readonly IModRepository modRepository;
    private readonly ILibraryService libraryService;
    private readonly IPreviewImageService previewImageService;
    private readonly IModOperationService modOperationService;
    private readonly DownloadQueueViewModel downloadQueue;
    private readonly ObservableCollection<ModListItemViewModel> items = [];
    private readonly AsyncRelayCommand refreshCommand;
    private readonly AsyncRelayCommand rescanCommand;
    private CancellationTokenSource? previewCancellation;
    private string? libraryRoot;
    private string searchText = string.Empty;
    private ModSortOption selectedSort = ModSortOption.Title;
    private ModListItemViewModel? selectedMod;
    private ImageSource? previewImage;
    private LibraryViewState state = LibraryViewState.Loading;
    private bool isBusy;
    private bool canModifyLibrary;
    private bool refreshLibraryOnStartup;
    private string? lastError;

    public MainWindowViewModel(
        ISettingsStore settingsStore,
        IModRepository modRepository,
        ILibraryService libraryService,
        IPreviewImageService previewImageService,
        IModOperationService modOperationService,
        DownloadQueueViewModel downloadQueue)
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
        downloadQueue.PropertyChanged += OnDownloadQueuePropertyChanged;

        ModsView = new ListCollectionView(items)
        {
            Filter = FilterMod,
        };
        ApplySort();
        refreshCommand = new AsyncRelayCommand(RefreshAsync, () => !IsBusy);
        rescanCommand = new AsyncRelayCommand(RescanAsync, CanRescan);
    }

    public ICollectionView ModsView { get; }

    public IAsyncRelayCommand RefreshCommand => refreshCommand;

    public IAsyncRelayCommand RescanCommand => rescanCommand;

    public string SearchText
    {
        get => searchText;
        set
        {
            if (SetProperty(ref searchText, value ?? string.Empty))
            {
                ModsView.Refresh();
            }
        }
    }

    public ModSortOption SelectedSort
    {
        get => selectedSort;
        set
        {
            if (SetProperty(ref selectedSort, value))
            {
                ApplySort();
            }
        }
    }

    public ModListItemViewModel? SelectedMod
    {
        get => selectedMod;
        set
        {
            if (SetProperty(ref selectedMod, value))
            {
                StartPreviewLoad(value);
                OnPropertyChanged(nameof(HasSelection));
                OnPropertyChanged(nameof(CanModifySelectedMod));
            }
        }
    }

    public ImageSource? PreviewImage
    {
        get => previewImage;
        private set => SetProperty(ref previewImage, value);
    }

    public LibraryViewState State
    {
        get => state;
        private set => SetProperty(ref state, value);
    }

    public bool IsBusy
    {
        get => isBusy;
        private set
        {
            if (SetProperty(ref isBusy, value))
            {
                OnPropertyChanged(nameof(CanRunWriteOperations));
                OnPropertyChanged(nameof(CanModifySelectedMod));
                refreshCommand.NotifyCanExecuteChanged();
                rescanCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool CanModifyLibrary
    {
        get => canModifyLibrary;
        private set
        {
            if (SetProperty(ref canModifyLibrary, value))
            {
                OnPropertyChanged(nameof(CanRunWriteOperations));
                OnPropertyChanged(nameof(CanModifySelectedMod));
                rescanCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool HasSelection => SelectedMod is not null;

    public bool CanRunWriteOperations => CanModifyLibrary && !IsBusy && !downloadQueue.IsBusy;

    public bool CanModifySelectedMod => CanRunWriteOperations && HasSelection;

    public string? LastError
    {
        get => lastError;
        private set => SetProperty(ref lastError, value);
    }

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

    public async Task RescanAsync(CancellationToken cancellationToken = default)
    {
        if (!CanRescan() || libraryRoot is null)
        {
            return;
        }

        IsBusy = true;
        State = LibraryViewState.Loading;
        LastError = null;
        try
        {
            var junction = await libraryService.EnsureJunctionAsync(
                libraryRoot,
                cancellationToken: cancellationToken);
            if (junction.Status != OperationStatus.Succeeded)
            {
                ClearItems();
                CanModifyLibrary = false;
                LastError = junction.Error;
                State = LibraryViewState.Error;
                return;
            }

            var result = await libraryService.ScanAsync(libraryRoot, cancellationToken: cancellationToken);
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
            IsBusy = false;
        }
    }

    public void Dispose()
    {
        downloadQueue.PropertyChanged -= OnDownloadQueuePropertyChanged;
        previewCancellation?.Cancel();
        previewCancellation?.Dispose();
        GC.SuppressFinalize(this);
    }

    private bool CanRescan() => !IsBusy && !downloadQueue.IsBusy && libraryRoot is not null;

    private async Task RefreshLibraryStateAsync(CancellationToken cancellationToken)
    {
        State = LibraryViewState.Loading;
        LastError = null;
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
                cancellationToken: cancellationToken);
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
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (IsOperationalFailure(exception))
        {
            ClearItems();
            CanModifyLibrary = false;
            LastError = exception.Message;
            State = LibraryViewState.Error;
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
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (IsOperationalFailure(exception))
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
            OnPropertyChanged(nameof(CanModifySelectedMod));
            rescanCommand.NotifyCanExecuteChanged();
        }
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
        catch (OperationCanceledException)
        {
            // A newer selection owns the preview area.
        }
    }

    private static bool IsOperationalFailure(Exception exception) =>
        exception is IOException
            or UnauthorizedAccessException
            or InvalidOperationException
            or InvalidDataException
            or ArgumentException
            or NotSupportedException
            or SqliteException;
}
