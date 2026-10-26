using System.ComponentModel;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using StellarisDownloader.App.ViewModels;
using StellarisDownloader.Core.Models;
using StellarisDownloader.Core.Persistence;
using StellarisDownloader.Core.Services;

namespace StellarisDownloader.App.Services;

internal sealed class WindowFactory(IServiceProvider services, Application application) : IWindowFactory
{
    private bool waitingForFavorites;

    public SettingsWindow CreateSettingsWindow(SettingsLoadResult settings, bool isInitialization) =>
        new(new SettingsViewModel(
            services.GetRequiredService<ISettingsStore>(), services.GetRequiredService<ILibraryService>(),
            services.GetRequiredService<ILocalizationService>(), services.GetRequiredService<WriteOperationCoordinator>(),
            settings.Settings, isInitialization, settings.CorruptBackupPath));

    private async Task<SettingsWindow> CreateRegularSettingsWindowAsync() =>
        CreateSettingsWindow(await services.GetRequiredService<ISettingsStore>().LoadAsync(), isInitialization: false);

    public MainWindow CreateMainWindow()
    {
        var window = new MainWindow(
            services.GetRequiredService<MainWindowViewModel>(),
            services.GetRequiredService<DownloadQueueViewModel>(),
            services.GetRequiredService<ApplicationUpdateViewModel>(),
            CreateRegularSettingsWindowAsync,
            () => new DownloadQueueWindow(services.GetRequiredService<DownloadQueueViewModel>()),
            () => new WorkshopBrowserWindow(services.GetRequiredService<DownloadQueueViewModel>(),
                services.GetRequiredService<IInstalledWorkshopStateProvider>(), services.GetRequiredService<WorkshopFavoritesViewModel>()),
            () => CreateUpdateWindow(CreateUpdateViewModel()),
            CreateApplicationUpdateWindow,
            () => new ModSnapshotsWindow(new ModSnapshotsViewModel(services.GetRequiredService<IModSnapshotService>(),
                services.GetRequiredService<ModSnapshotHistory>(), key => application.TryFindResource(key) as string ?? key)),
            disposeViewModel: false);
        window.Closing += OnMainWindowClosing;
        return window;
    }

    public UpdateSelectionViewModel CreateUpdateViewModel() =>
        ActivatorUtilities.CreateInstance<UpdateSelectionViewModel>(services);

    public UpdateSelectionWindow CreateUpdateWindow(UpdateSelectionViewModel viewModel) => new(viewModel);

    public ApplicationUpdateWindow CreateApplicationUpdateWindow() =>
        new(services.GetRequiredService<ApplicationUpdateViewModel>(), disposeViewModel: false);

    public Task PrepareForAppUpdateRestartAsync() => application.Dispatcher.InvokeAsync(() =>
    {
        if (services.GetRequiredService<WriteOperationCoordinator>().IsBusy)
        {
            throw new StellarisOperationException("Wait for the active write operation before applying the update.");
        }

        ((MainWindow)application.MainWindow).PrepareForAppUpdateRestart();
        Log.Information("Restarting to apply an application update.");
    }).Task;

    private async void OnMainWindowClosing(object? sender, CancelEventArgs args)
    {
        var favorites = services.GetRequiredService<WorkshopFavoritesViewModel>();
        if (args.Cancel || !favorites.IsBusy) { return; }
        args.Cancel = true;
        if (waitingForFavorites) { return; }
        waitingForFavorites = true;
        try
        {
            await favorites.FlushAsync();
            if (favorites.LastError is null) { ((Window)sender!).Close(); }
        }
        finally
        {
            waitingForFavorites = false;
        }
    }
}
