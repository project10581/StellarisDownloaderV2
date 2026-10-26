using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using StellarisDownloader.App.Services;
using StellarisDownloader.App.ViewModels;
using StellarisDownloader.Core.Models;
using StellarisDownloader.Core.Persistence;
using StellarisDownloader.Core.Services;

namespace StellarisDownloader.App;

public partial class App : Application, IDisposable
{
    private ServiceProvider? services;
    private bool disposed;

    public App()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
    }

    private void OnDispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
    {
        ReportUnhandledException(e.Exception);
        e.Handled = true;
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        ReportUnhandledException(e.Exception);
        e.SetObserved();
    }

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e) =>
        ReportUnhandledException((Exception)e.ExceptionObject);

    private static void ReportUnhandledException(Exception exception)
    {
        Log.Error(exception, "An unhandled application error occurred.");
        MessageBox.Show(exception.Message, "Stellaris Downloader", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        SettingsLoadResult? settings;
        try
        {
            settings = await InitializeCoreAsync();
            settings = await RunFirstTimeSetupIfNeededAsync(settings);
            if (settings is null)
            {
                Shutdown();
                return;
            }

            await ShowMainWindowAsync(settings);
        }
        catch (Exception exception)
        {
            Log.Error(exception, "Application startup failed.");
            MessageBox.Show(exception.Message, "Stellaris Downloader", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(-1);
            return;
        }

        await RunStartupChecksAsync(
            () => CheckModUpdatesAsync(settings.Settings),
            () => CheckApplicationUpdatesAsync(settings.Settings));
    }

    private async Task<SettingsLoadResult> InitializeCoreAsync()
    {
        Log.Information("Initializing application services.");
        var paths = AppDataPaths.CreateDefault();
        paths.EnsureDirectories();
        services = ServiceRegistration.Create(this, paths);
        await services.GetRequiredService<IModRepository>().InitializeAsync();
        var settingsStore = services.GetRequiredService<ISettingsStore>();
        var settings = await settingsStore.LoadAsync();
        services.GetRequiredService<ILocalizationService>().SetLanguage(settings.Settings.Language);
        return settings;
    }

    private Task<SettingsLoadResult?> RunFirstTimeSetupIfNeededAsync(SettingsLoadResult settings)
    {
        if (!settings.RequiresInitialization && !string.IsNullOrWhiteSpace(settings.Settings.LibraryRoot))
        {
            return Task.FromResult<SettingsLoadResult?>(settings);
        }

        var window = services!.GetRequiredService<IWindowFactory>().CreateSettingsWindow(settings, isInitialization: true);
        window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        return Task.FromResult(window.ShowDialog() is true
            ? new SettingsLoadResult(((SettingsViewModel)window.DataContext).SavedSettings, false, settings.CorruptBackupPath)
            : null);
    }

    private async Task ShowMainWindowAsync(SettingsLoadResult settings)
    {
        services!.GetRequiredService<ILocalizationService>().SetLanguage(settings.Settings.Language);
        var window = services!.GetRequiredService<IWindowFactory>().CreateMainWindow();
        MainWindow = window;
        window.Show();
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        await services!.GetRequiredService<MainWindowViewModel>().InitializeAsync();
    }

    internal static async Task RunStartupChecksAsync(Func<Task> checkModUpdates, Func<Task> checkApplicationUpdates)
    {
        try
        {
            await checkModUpdates();
        }
        catch (Exception exception)
        {
            Log.Warning(exception, "Automatic mod update check failed.");
        }

        try
        {
            await checkApplicationUpdates();
        }
        catch (Exception exception)
        {
            Log.Warning(exception, "Automatic application update check failed.");
        }
    }

    private async Task CheckModUpdatesAsync(AppSettings settings)
    {
        var viewModel = services!.GetRequiredService<MainWindowViewModel>();
        if (!settings.CheckModUpdatesOnStartup || !viewModel.CanRunWriteOperations) { return; }
        var factory = services!.GetRequiredService<IWindowFactory>();
        using var updates = factory.CreateUpdateViewModel();
        await updates.CheckUpdatesAsync();
        if (updates.LastError is { } error)
        {
            Log.Warning("Automatic mod update check failed: {Error}", error);
        }
        else if (updates.Items.Any(item => item.State != UpdateState.UpToDate))
        {
            var window = factory.CreateUpdateWindow(updates);
            window.Owner = MainWindow;
            window.ShowDialog();
            await viewModel.RefreshAsync();
        }
    }

    private async Task CheckApplicationUpdatesAsync(AppSettings settings)
    {
        if (!settings.CheckAppUpdatesOnStartup) { return; }
        var updates = services!.GetRequiredService<ApplicationUpdateViewModel>();
        await updates.CheckAsync();
        if (updates.LastError is { } error)
        {
            Log.Warning("Automatic application update check failed: {Error}", error);
        }
        else if (updates.IsUpdateAvailable)
        {
            var window = services!.GetRequiredService<IWindowFactory>().CreateApplicationUpdateWindow();
            window.Owner = MainWindow;
            window.ShowDialog();
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
        if (disposed) { return; }
        disposed = true;
        DispatcherUnhandledException -= OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;
        AppDomain.CurrentDomain.UnhandledException -= OnDomainUnhandledException;
        services?.Dispose();
        GC.SuppressFinalize(this);
    }
}
