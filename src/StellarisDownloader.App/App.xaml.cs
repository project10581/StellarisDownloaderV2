using System.IO;
using System.Net.Http;
using System.Windows;
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
    private HttpClient? previewHttpClient;
    private bool disposed;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        try
        {
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
            var libraryService = new LibraryService(
                settingsStore,
                modRepository,
                new WindowsJunctionManager(),
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

            previewHttpClient = new HttpClient();
            var viewModel = new MainWindowViewModel(
                settingsStore,
                modRepository,
                libraryService,
                new PreviewImageService(previewHttpClient));
            var mainWindow = new MainWindow(viewModel, CreateRegularSettingsWindowAsync);
            MainWindow = mainWindow;
            mainWindow.Show();
            ShutdownMode = ShutdownMode.OnMainWindowClose;
            await viewModel.InitializeAsync().ConfigureAwait(true);
        }
        catch (Exception exception)
        {
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
        previewHttpClient?.Dispose();
        writeCoordinator?.Dispose();
        modRepository?.Dispose();
        settingsStore?.Dispose();
        previewHttpClient = null;
        writeCoordinator = null;
        modRepository = null;
        settingsStore = null;
        GC.SuppressFinalize(this);
    }
}
