using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Data.Sqlite;
using StellarisDownloader.App.Services;
using StellarisDownloader.Core.Models;
using StellarisDownloader.Core.Services;

namespace StellarisDownloader.App.ViewModels;

public sealed class SettingsViewModel : ObservableObject, IDisposable
{
    private readonly ISettingsStore settingsStore;
    private readonly ILibraryService libraryService;
    private readonly ILocalizationService localizationService;
    private readonly WriteOperationCoordinator writeCoordinator;
    private AppSettings committedSettings;
    private string libraryRoot;
    private string selectedLanguage;
    private bool refreshLibraryOnStartup;
    private bool checkModUpdatesOnStartup;
    private bool checkAppUpdatesOnStartup;
    private bool isBusy;
    private bool disposed;
    private string? errorMessage;

    public SettingsViewModel(
        ISettingsStore settingsStore,
        ILibraryService libraryService,
        ILocalizationService localizationService,
        WriteOperationCoordinator writeCoordinator,
        AppSettings settings,
        bool isInitialization,
        string? corruptBackupPath = null)
    {
        ArgumentNullException.ThrowIfNull(settingsStore);
        ArgumentNullException.ThrowIfNull(libraryService);
        ArgumentNullException.ThrowIfNull(localizationService);
        ArgumentNullException.ThrowIfNull(writeCoordinator);
        ArgumentNullException.ThrowIfNull(settings);

        this.settingsStore = settingsStore;
        this.libraryService = libraryService;
        this.localizationService = localizationService;
        this.writeCoordinator = writeCoordinator;
        committedSettings = settings;
        libraryRoot = settings.LibraryRoot ?? string.Empty;
        selectedLanguage = settings.Language;
        refreshLibraryOnStartup = settings.RefreshLibraryOnStartup;
        checkModUpdatesOnStartup = settings.CheckModUpdatesOnStartup;
        checkAppUpdatesOnStartup = settings.CheckAppUpdatesOnStartup;
        IsInitialization = isInitialization;
        CorruptBackupPath = corruptBackupPath;
    }

    public bool IsInitialization { get; }

    public string? CorruptBackupPath { get; }

    public string LibraryRoot
    {
        get => libraryRoot;
        set => SetProperty(ref libraryRoot, value ?? string.Empty);
    }

    public string SelectedLanguage
    {
        get => selectedLanguage;
        set
        {
            if (SetProperty(ref selectedLanguage, value))
            {
                localizationService.SetLanguage(value);
            }
        }
    }

    public bool RefreshLibraryOnStartup
    {
        get => refreshLibraryOnStartup;
        set => SetProperty(ref refreshLibraryOnStartup, value);
    }

    public bool CheckModUpdatesOnStartup
    {
        get => checkModUpdatesOnStartup;
        set => SetProperty(ref checkModUpdatesOnStartup, value);
    }

    public bool CheckAppUpdatesOnStartup
    {
        get => checkAppUpdatesOnStartup;
        set => SetProperty(ref checkAppUpdatesOnStartup, value);
    }

    public bool IsBusy
    {
        get => isBusy;
        private set => SetProperty(ref isBusy, value);
    }

    public string? ErrorMessage
    {
        get => errorMessage;
        private set => SetProperty(ref errorMessage, value);
    }

    public async Task<SettingsSaveResult> SaveAsync(CancellationToken cancellationToken = default)
    {
        if (IsBusy)
        {
            return Failed("Settings are already being saved.");
        }

        IsBusy = true;
        ErrorMessage = null;
        try
        {
            var validation = await libraryService.ValidateAsync(LibraryRoot, cancellationToken);
            if (!validation.IsValid || validation.NormalizedPath is null)
            {
                return SetFailure(validation.Error ?? "The mod library folder is invalid.");
            }

            var proposedSettings = new AppSettings
            {
                LibraryRoot = validation.NormalizedPath,
                Language = SelectedLanguage,
                RefreshLibraryOnStartup = RefreshLibraryOnStartup,
                CheckModUpdatesOnStartup = CheckModUpdatesOnStartup,
                CheckAppUpdatesOnStartup = CheckAppUpdatesOnStartup,
            };

            if (LibraryRootsEqual(committedSettings.LibraryRoot, validation.NormalizedPath))
            {
                await writeCoordinator.ExecuteAsync(
                    async token =>
                    {
                        await settingsStore.SaveAsync(proposedSettings, token);
                        return true;
                    },
                    cancellationToken);
                Commit(proposedSettings);
                return new SettingsSaveResult(
                    Succeeded: true,
                    SettingsCommitted: true,
                    RequiresScanRetry: false,
                    RequiresManualRepair: false,
                    Summary: null,
                    Error: null);
            }

            var switchResult = await libraryService.SwitchAsync(
                proposedSettings,
                cancellationToken: cancellationToken);
            if (switchResult.Status == OperationStatus.Succeeded)
            {
                Commit(proposedSettings with { LibraryRoot = switchResult.RequestedLibraryRoot });
                var scan = switchResult.ScanResult;
                var summary = scan is null
                    ? null
                    : new LibrarySwitchSummary(
                        scan.AddedWorkshopIds.Count,
                        scan.RemovedWorkshopIds.Count,
                        scan.EmptyWorkshopIds.Count,
                        scan.IgnoredDirectoryCount);
                return new SettingsSaveResult(
                    Succeeded: true,
                    SettingsCommitted: true,
                    RequiresScanRetry: false,
                    RequiresManualRepair: false,
                    Summary: summary,
                    Error: null);
            }

            if (switchResult.SettingsCommitted)
            {
                Commit(proposedSettings with { LibraryRoot = switchResult.RequestedLibraryRoot });
                ErrorMessage = switchResult.Error;
                return new SettingsSaveResult(
                    Succeeded: true,
                    SettingsCommitted: true,
                    RequiresScanRetry: switchResult.CanRetryScan,
                    RequiresManualRepair: switchResult.RequiresManualRepair,
                    Summary: null,
                    Error: switchResult.Error);
            }

            return SetFailure(
                switchResult.Error ?? "The mod library could not be switched.",
                switchResult.RequiresManualRepair);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (IsOperationalFailure(exception))
        {
            return SetFailure(exception.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    public void Cancel()
    {
        localizationService.SetLanguage(committedSettings.Language);
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        Cancel();
        GC.SuppressFinalize(this);
    }

    private void Commit(AppSettings settings)
    {
        committedSettings = settings;
        LibraryRoot = settings.LibraryRoot ?? string.Empty;
        ErrorMessage = null;
    }

    private SettingsSaveResult SetFailure(string error, bool requiresManualRepair = false)
    {
        ErrorMessage = error;
        return Failed(error, requiresManualRepair);
    }

    private static SettingsSaveResult Failed(string error, bool requiresManualRepair = false) =>
        new(
            Succeeded: false,
            SettingsCommitted: false,
            RequiresScanRetry: false,
            RequiresManualRepair: requiresManualRepair,
            Summary: null,
            Error: error);

    private static bool LibraryRootsEqual(string? left, string right)
    {
        if (string.IsNullOrWhiteSpace(left))
        {
            return false;
        }

        try
        {
            return string.Equals(
                NormalizePath(left),
                NormalizePath(right),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
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

    private static string NormalizePath(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
}
