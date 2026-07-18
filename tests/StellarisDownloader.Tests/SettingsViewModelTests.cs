using StellarisDownloader.App.ViewModels;
using StellarisDownloader.Core.Models;
using StellarisDownloader.Core.Services;

namespace StellarisDownloader.Tests;

public sealed class SettingsViewModelTests
{
    [Fact]
    public async Task UnchangedRootSavesTheCompleteSettingsObjectExactlyOnce()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var root = temporaryDirectory.GetPath("library");
        Directory.CreateDirectory(root);
        var settingsStore = new StubSettingsStore(new AppSettings { LibraryRoot = root });
        var library = new StubLibraryService
        {
            ValidationResult = new LibraryValidationResult(true, root, Error: null),
        };
        var localization = new StubLocalizationService();
        using var coordinator = new WriteOperationCoordinator();
        using var viewModel = new SettingsViewModel(
            settingsStore,
            library,
            localization,
            coordinator,
            settingsStore.Settings,
            isInitialization: false);
        viewModel.SelectedLanguage = AppSettings.SimplifiedChineseLanguage;
        viewModel.RefreshLibraryOnStartup = true;
        viewModel.CheckModUpdatesOnStartup = true;
        viewModel.CheckAppUpdatesOnStartup = true;

        var result = await viewModel.SaveAsync();

        Assert.True(result.Succeeded);
        Assert.Equal(1, settingsStore.SaveCount);
        Assert.Equal(0, library.SwitchCount);
        Assert.Equal(AppSettings.SimplifiedChineseLanguage, settingsStore.Settings.Language);
        Assert.True(settingsStore.Settings.RefreshLibraryOnStartup);
        Assert.True(settingsStore.Settings.CheckModUpdatesOnStartup);
        Assert.True(settingsStore.Settings.CheckAppUpdatesOnStartup);
    }

    [Fact]
    public async Task ChangedRootUsesOnlyLibrarySwitchAndReturnsItsSummary()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var oldRoot = temporaryDirectory.GetPath("old");
        var newRoot = temporaryDirectory.GetPath("new");
        var settingsStore = new StubSettingsStore(new AppSettings { LibraryRoot = oldRoot });
        var library = new StubLibraryService
        {
            ValidationResult = new LibraryValidationResult(true, newRoot, Error: null),
            SwitchResult = new LibrarySwitchResult
            {
                Status = OperationStatus.Succeeded,
                RequestedLibraryRoot = newRoot,
                SettingsCommitted = true,
                ScanResult = new LibraryScanResult
                {
                    Status = OperationStatus.Succeeded,
                    LibraryRoot = newRoot,
                    AddedWorkshopIds = ["1", "2"],
                    RemovedWorkshopIds = ["3"],
                    EmptyWorkshopIds = ["4", "5", "6"],
                    IgnoredDirectoryCount = 4,
                },
            },
        };
        using var coordinator = new WriteOperationCoordinator();
        using var viewModel = new SettingsViewModel(
            settingsStore,
            library,
            new StubLocalizationService(),
            coordinator,
            settingsStore.Settings,
            isInitialization: false)
        {
            LibraryRoot = newRoot,
        };

        var result = await viewModel.SaveAsync();

        Assert.True(result.Succeeded);
        Assert.Equal(0, settingsStore.SaveCount);
        Assert.Equal(1, library.SwitchCount);
        Assert.Equal(newRoot, library.LastProposedSettings?.LibraryRoot);
        Assert.Equal(new LibrarySwitchSummary(2, 1, 3, 4), result.Summary);
    }

    [Fact]
    public async Task EquivalentNormalizedRootDoesNotTriggerLibrarySwitch()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var root = temporaryDirectory.GetPath("library");
        Directory.CreateDirectory(root);
        var settingsStore = new StubSettingsStore(new AppSettings
        {
            LibraryRoot = root + Path.DirectorySeparatorChar,
        });
        var library = new StubLibraryService
        {
            ValidationResult = new LibraryValidationResult(true, root, Error: null),
        };
        using var coordinator = new WriteOperationCoordinator();
        using var viewModel = new SettingsViewModel(
            settingsStore,
            library,
            new StubLocalizationService(),
            coordinator,
            settingsStore.Settings,
            isInitialization: false)
        {
            LibraryRoot = root,
        };

        var result = await viewModel.SaveAsync();

        Assert.True(result.Succeeded);
        Assert.Equal(1, settingsStore.SaveCount);
        Assert.Equal(0, library.SwitchCount);
    }

    [Fact]
    public async Task InvalidRootDoesNotSaveOrSwitch()
    {
        var settingsStore = new StubSettingsStore(new AppSettings());
        var library = new StubLibraryService
        {
            ValidationResult = new LibraryValidationResult(false, null, "Invalid root."),
        };
        using var coordinator = new WriteOperationCoordinator();
        using var viewModel = new SettingsViewModel(
            settingsStore,
            library,
            new StubLocalizationService(),
            coordinator,
            settingsStore.Settings,
            isInitialization: true)
        {
            LibraryRoot = "invalid",
        };

        var result = await viewModel.SaveAsync();

        Assert.False(result.Succeeded);
        Assert.Equal("Invalid root.", result.Error);
        Assert.Equal(0, settingsStore.SaveCount);
        Assert.Equal(0, library.SwitchCount);
    }

    [Fact]
    public void LanguageChangesImmediatelyAndCancelRestoresTheCommittedLanguage()
    {
        var settingsStore = new StubSettingsStore(new AppSettings { LibraryRoot = "C:\\Mods" });
        var localization = new StubLocalizationService();
        using var coordinator = new WriteOperationCoordinator();
        using var viewModel = new SettingsViewModel(
            settingsStore,
            new StubLibraryService(),
            localization,
            coordinator,
            settingsStore.Settings,
            isInitialization: false);

        viewModel.SelectedLanguage = AppSettings.SimplifiedChineseLanguage;
        Assert.Equal(AppSettings.SimplifiedChineseLanguage, localization.CurrentLanguage);

        viewModel.Cancel();

        Assert.Equal(AppSettings.DefaultLanguage, localization.CurrentLanguage);
    }

    [Fact]
    public async Task CommittedSwitchWithFailedScanKeepsNewSettingsAndRequestsRescan()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var oldRoot = temporaryDirectory.GetPath("old");
        var newRoot = temporaryDirectory.GetPath("new");
        var settingsStore = new StubSettingsStore(new AppSettings { LibraryRoot = oldRoot });
        var library = new StubLibraryService
        {
            ValidationResult = new LibraryValidationResult(true, newRoot, Error: null),
            SwitchResult = new LibrarySwitchResult
            {
                Status = OperationStatus.Failed,
                RequestedLibraryRoot = newRoot,
                SettingsCommitted = true,
                CanRetryScan = true,
                Error = "Forced scan failure.",
            },
        };
        var localization = new StubLocalizationService();
        using var coordinator = new WriteOperationCoordinator();
        using var viewModel = new SettingsViewModel(
            settingsStore,
            library,
            localization,
            coordinator,
            settingsStore.Settings,
            isInitialization: false)
        {
            LibraryRoot = newRoot,
            SelectedLanguage = AppSettings.SimplifiedChineseLanguage,
        };

        var result = await viewModel.SaveAsync();
        viewModel.Cancel();

        Assert.True(result.Succeeded);
        Assert.True(result.SettingsCommitted);
        Assert.True(result.RequiresScanRetry);
        Assert.Equal("Forced scan failure.", result.Error);
        Assert.Equal(AppSettings.SimplifiedChineseLanguage, localization.CurrentLanguage);
    }

    [Fact]
    public async Task FailedJunctionRollbackIsReportedAsManualRepair()
    {
        var settingsStore = new StubSettingsStore(new AppSettings { LibraryRoot = "C:\\Old" });
        var library = new StubLibraryService
        {
            ValidationResult = new LibraryValidationResult(true, "C:\\New", Error: null),
            SwitchResult = new LibrarySwitchResult
            {
                Status = OperationStatus.Failed,
                RequestedLibraryRoot = "C:\\New",
                SettingsCommitted = false,
                RequiresManualRepair = true,
                Error = "Rollback failed.",
            },
        };
        using var coordinator = new WriteOperationCoordinator();
        using var viewModel = new SettingsViewModel(
            settingsStore,
            library,
            new StubLocalizationService(),
            coordinator,
            settingsStore.Settings,
            isInitialization: false)
        {
            LibraryRoot = "C:\\New",
        };

        var result = await viewModel.SaveAsync();

        Assert.False(result.Succeeded);
        Assert.True(result.RequiresManualRepair);
    }

    [Fact]
    public async Task ConcurrentSaveClickDoesNotStartASecondWrite()
    {
        var settingsStore = new StubSettingsStore(new AppSettings { LibraryRoot = "C:\\Mods" })
        {
            PendingSave = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously),
        };
        var library = new StubLibraryService
        {
            ValidationResult = new LibraryValidationResult(true, "C:\\Mods", Error: null),
        };
        using var coordinator = new WriteOperationCoordinator();
        using var viewModel = new SettingsViewModel(
            settingsStore,
            library,
            new StubLocalizationService(),
            coordinator,
            settingsStore.Settings,
            isInitialization: false);

        var firstSave = viewModel.SaveAsync();
        var secondResult = await viewModel.SaveAsync();

        Assert.False(secondResult.Succeeded);
        Assert.Equal(1, settingsStore.SaveCount);

        settingsStore.PendingSave.SetResult(true);
        var firstResult = await firstSave;
        Assert.True(firstResult.Succeeded);
    }
}
