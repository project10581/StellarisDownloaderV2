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
    private string? lastError;

    public MainWindowViewModel(
        ISettingsStore settingsStore,
        IModRepository modRepository,
        ILibraryService libraryService,
        IPreviewImageService previewImageService)
    {
        ArgumentNullException.ThrowIfNull(settingsStore);
        ArgumentNullException.ThrowIfNull(modRepository);
        ArgumentNullException.ThrowIfNull(libraryService);
        ArgumentNullException.ThrowIfNull(previewImageService);

        this.settingsStore = settingsStore;
        this.modRepository = modRepository;
        this.libraryService = libraryService;
        this.previewImageService = previewImageService;

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
                rescanCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool HasSelection => SelectedMod is not null;

    public bool CanRunWriteOperations => CanModifyLibrary && !IsBusy;

    public string? LastError
    {
        get => lastError;
        private set => SetProperty(ref lastError, value);
    }

    public Task InitializeAsync(CancellationToken cancellationToken = default) =>
        RefreshAsync(cancellationToken);

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        State = LibraryViewState.Loading;
        LastError = null;
        try
        {
            var settings = await settingsStore.LoadAsync(cancellationToken);
            libraryRoot = settings.Settings.LibraryRoot;
            if (settings.RequiresInitialization || string.IsNullOrWhiteSpace(libraryRoot))
            {
                ClearItems();
                CanModifyLibrary = false;
                State = LibraryViewState.NoLibrary;
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
        finally
        {
            IsBusy = false;
        }
    }

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
        previewCancellation?.Cancel();
        previewCancellation?.Dispose();
        GC.SuppressFinalize(this);
    }

    private bool CanRescan() => !IsBusy && CanModifyLibrary && libraryRoot is not null;

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
