using System.IO;
using System.Net.Http;
using System.Windows;
using Serilog;
using StellarisDownloader.App.Services;
using StellarisDownloader.App.ViewModels;
using StellarisDownloader.Core.Integrations;
using StellarisDownloader.Core.Models;
using StellarisDownloader.Core.Persistence;
using StellarisDownloader.Core.Services;

namespace StellarisDownloader.App;

public partial class App : Application, IDisposable
{
    private JsonSettingsStore? settingsStore;
    private SqliteModRepository? modRepository;
    private WriteOperationCoordinator? writeCoordinator;
    private HttpClient? httpClient;
    private SteamCmdService? steamCmdService;
    private DownloadQueueViewModel? downloadQueueViewModel;
    private bool disposed;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        try
        {
            Log.Information("Initializing application services.");
            var paths = AppDataPaths.CreateDefault();
            paths.EnsureDirectories();

            settingsStore = new JsonSettingsStore(paths.SettingsFile);
            modRepository = new SqliteModRepository(paths.DatabaseFile);
            await modRepository.InitializeAsync().ConfigureAwait(true);

            var loadedSettings = await settingsStore.LoadAsync().ConfigureAwait(true);
            var localizationService = new LocalizationService(Resources);
            localizationService.SetLanguage(loadedSettings.Settings.Language);

            writeCoordinator = new WriteOperationCoordinator();
            var junctionPath = Path.Combine(
                paths.SteamCmdDirectory,
                "steamapps",
                "workshop",
                "content",
                "281990");
            var junctionManager = new WindowsJunctionManager();
            var libraryService = new LibraryService(
                settingsStore,
                modRepository,
                junctionManager,
                writeCoordinator,
                junctionPath);

            SettingsWindow CreateSettingsWindow(
                SettingsLoadResult settingsResult,
                bool isInitialization)
            {
                var settingsViewModel = new SettingsViewModel(
                    settingsStore,
                    libraryService,
                    localizationService,
                    writeCoordinator,
                    settingsResult.Settings,
                    isInitialization,
                    settingsResult.CorruptBackupPath);
                return new SettingsWindow(settingsViewModel);
            }

            if (loadedSettings.RequiresInitialization
                || string.IsNullOrWhiteSpace(loadedSettings.Settings.LibraryRoot))
            {
                var setupWindow = CreateSettingsWindow(loadedSettings, isInitialization: true);
                setupWindow.WindowStartupLocation = WindowStartupLocation.CenterScreen;
                if (setupWindow.ShowDialog() is not true)
                {
                    Shutdown();
                    return;
                }
            }

            async Task<SettingsWindow> CreateRegularSettingsWindowAsync()
            {
                var currentSettings = await settingsStore.LoadAsync().ConfigureAwait(true);
                return CreateSettingsWindow(currentSettings, isInitialization: false);
            }

            httpClient = new HttpClient();
            var workshopClient = new WorkshopClient(httpClient);
            steamCmdService = new SteamCmdService(
                httpClient,
                new ProcessRunner(),
                paths.SteamCmdDirectory);
            var modOperationService = new ModOperationService(
                modRepository,
                steamCmdService,
                workshopClient,
                junctionManager,
                new WindowsFileDeletionService(),
                writeCoordinator,
                junctionPath);
            downloadQueueViewModel = new DownloadQueueViewModel(
                settingsStore,
                workshopClient,
                modOperationService);
            var installedStateProvider = new InstalledWorkshopStateProvider(
                settingsStore,
                modRepository);
            var viewModel = new MainWindowViewModel(
                settingsStore,
                modRepository,
                libraryService,
                new PreviewImageService(httpClient),
                modOperationService,
                downloadQueueViewModel);
            UpdateSelectionWindow CreateUpdateWindow() =>
                new(new UpdateSelectionViewModel(settingsStore, modOperationService));

            var mainWindow = new MainWindow(
                viewModel,
                downloadQueueViewModel,
                CreateRegularSettingsWindowAsync,
                () => new DownloadQueueWindow(downloadQueueViewModel),
                () => new WorkshopBrowserWindow(
                    downloadQueueViewModel,
                    installedStateProvider),
                CreateUpdateWindow);
            MainWindow = mainWindow;
            mainWindow.Show();
            ShutdownMode = ShutdownMode.OnMainWindowClose;
            await viewModel.InitializeAsync().ConfigureAwait(true);

            var startupSettings = await settingsStore.LoadAsync().ConfigureAwait(true);
            if (startupSettings.Settings.CheckModUpdatesOnStartup
                && viewModel.CanRunWriteOperations)
            {
                var startupUpdates = new UpdateSelectionViewModel(
                    settingsStore,
                    modOperationService);
                await startupUpdates.CheckUpdatesAsync().ConfigureAwait(true);
                if (startupUpdates.Items.Any(item => item.State != UpdateState.UpToDate))
                {
                    var updateWindow = new UpdateSelectionWindow(startupUpdates)
                    {
                        Owner = mainWindow,
                    };
                    updateWindow.ShowDialog();
                    await viewModel.RefreshAsync().ConfigureAwait(true);
                }
                else
                {
                    startupUpdates.Dispose();
                }
            }
        }
        catch (Exception exception)
        {
            Log.Error(exception, "Application startup failed.");
            MessageBox.Show(
                exception.Message,
                "Stellaris Downloader",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(-1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.Information("Stellaris Downloader V2 is exiting with code {ExitCode}.", e.ApplicationExitCode);
        Dispose();
        base.OnExit(e);
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        downloadQueueViewModel?.Dispose();
        steamCmdService?.Dispose();
        httpClient?.Dispose();
        writeCoordinator?.Dispose();
        modRepository?.Dispose();
        settingsStore?.Dispose();
        downloadQueueViewModel = null;
        steamCmdService = null;
        httpClient = null;
        writeCoordinator = null;
        modRepository = null;
        settingsStore = null;
        GC.SuppressFinalize(this);
    }
}
