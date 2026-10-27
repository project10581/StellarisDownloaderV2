using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Data.Sqlite;
using StellarisDownloader.App.Services;
using StellarisDownloader.Core.Models;
using StellarisDownloader.Core.Services;

namespace StellarisDownloader.App.ViewModels;

public sealed partial class SettingsViewModel : ObservableObject, IDisposable
{
    private readonly ISettingsStore settingsStore;
    private readonly ILibraryService libraryService;
    private readonly ILocalizationService localizationService;
    private readonly WriteOperationCoordinator writeCoordinator;
    private AppSettings committedSettings;
    private readonly IDialogService dialogs;
    private readonly Func<string, string> text;
    private bool disposed;

    public SettingsViewModel(
        ISettingsStore settingsStore,
        ILibraryService libraryService,
        ILocalizationService localizationService,
        WriteOperationCoordinator writeCoordinator,
        AppSettings settings,
        bool isInitialization,
        string? corruptBackupPath = null,
        IDialogService? dialogs = null,
        Func<string, string>? text = null)
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
        this.dialogs = dialogs ?? new DialogService();
        this.text = text ?? (key => Application.Current?.TryFindResource(key) as string ?? key);
        LibraryRoot = settings.LibraryRoot ?? string.Empty;
        SelectedLanguage = settings.Language;
        RefreshLibraryOnStartup = settings.RefreshLibraryOnStartup;
        CheckModUpdatesOnStartup = settings.CheckModUpdatesOnStartup;
        CheckAppUpdatesOnStartup = settings.CheckAppUpdatesOnStartup;
        IsInitialization = isInitialization;
        CorruptBackupPath = corruptBackupPath;
    }

    public bool IsInitialization { get; }

    public AppSettings SavedSettings => committedSettings;

    public string? CorruptBackupPath { get; }

    public event Action<SettingsSaveResult>? Saved;
    public event Action? Cancelled;

    [ObservableProperty]
    public partial string LibraryRoot { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string SelectedLanguage { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool RefreshLibraryOnStartup { get; set; }

    [ObservableProperty]
    public partial bool CheckModUpdatesOnStartup { get; set; }

    [ObservableProperty]
    public partial bool CheckAppUpdatesOnStartup { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand), nameof(CancelChangesCommand))]
    public partial bool IsBusy { get; private set; }

    [ObservableProperty]
    public partial string? ErrorMessage { get; private set; }

    partial void OnLibraryRootChanged(string value)
    {
        if (value is null) { LibraryRoot = string.Empty; }
    }

    partial void OnSelectedLanguageChanged(string value) => localizationService.SetLanguage(value);

    private bool IsIdle => !IsBusy;

    [RelayCommand(CanExecute = nameof(IsIdle), AllowConcurrentExecutions = false)]
    private async Task Save()
    {
        if (!dialogs.Confirm(text("Settings.ConfirmMessage"), text("Settings.ConfirmTitle"))) { return; }
        var result = await SaveAsync();
        if (result.Succeeded) { Saved?.Invoke(result); }
        else { dialogs.ShowError(result.Error ?? text("Settings.SaveFailed"), text("Settings.ErrorTitle")); }
    }

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private void CancelChanges()
    {
        Cancel();
        Cancelled?.Invoke();
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
                return Commit(proposedSettings);
            }

            var switchResult = await libraryService.SwitchAsync(
                proposedSettings,
                cancellationToken: cancellationToken);
            if (switchResult.Status == OperationStatus.Succeeded)
            {
                var scan = switchResult.ScanResult;
                var summary = scan is null
                    ? null
                    : new LibrarySwitchSummary(
                        scan.AddedWorkshopIds.Count,
                        scan.RemovedWorkshopIds.Count,
                        scan.EmptyWorkshopIds.Count,
                        scan.IgnoredDirectoryCount);
                return Commit(proposedSettings with { LibraryRoot = switchResult.RequestedLibraryRoot }, summary);
            }

            if (switchResult.SettingsCommitted)
            {
                return Commit(proposedSettings with { LibraryRoot = switchResult.RequestedLibraryRoot },
                    error: switchResult.Error, requiresScanRetry: switchResult.CanRetryScan,
                    requiresManualRepair: switchResult.RequiresManualRepair);
            }

            return SetFailure(
                switchResult.Error ?? "The mod library could not be switched.",
                switchResult.RequiresManualRepair);
        }
        catch (OperationCanceledException exception) when (!OperationalFailure.IsTimeout(exception, cancellationToken))
        {
            throw;
        }
        catch (Exception exception) when (OperationalFailure.IsRecoverable(exception, cancellationToken))
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

    private SettingsSaveResult Commit(AppSettings settings, LibrarySwitchSummary? summary = null,
        string? error = null, bool requiresScanRetry = false, bool requiresManualRepair = false)
    {
        committedSettings = settings;
        LibraryRoot = settings.LibraryRoot ?? string.Empty;
        ErrorMessage = error;
        return new SettingsSaveResult(Succeeded: true, SettingsCommitted: true,
            requiresScanRetry, requiresManualRepair, summary, error);
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

        string normalizedLeft;
        try
        {
            normalizedLeft = Path.GetFullPath(left);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // Path.GetFullPath rejects malformed user-entered library paths.
            return false;
        }

        string normalizedRight;
        try
        {
            normalizedRight = Path.GetFullPath(right);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // Path.GetFullPath rejects malformed user-entered library paths.
            return false;
        }

        return PathUtil.Equal(normalizedLeft, normalizedRight);
    }

}
