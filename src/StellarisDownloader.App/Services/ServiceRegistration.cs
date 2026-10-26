using System.IO;
using System.Net.Http;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using StellarisDownloader.App.ViewModels;
using StellarisDownloader.Core.Integrations;
using StellarisDownloader.Core.Persistence;
using StellarisDownloader.Core.Services;

namespace StellarisDownloader.App.Services;

internal static class ServiceRegistration
{
    public static ServiceProvider Create(Application application, AppDataPaths paths)
    {
        var services = new ServiceCollection();
        var junctionPath = Path.Combine(paths.SteamCmdDirectory, "steamapps", "workshop", "content", "281990");
        services.AddSingleton(application);
        services.AddSingleton(paths);
        services.AddSingleton<ISettingsStore>(_ => new JsonSettingsStore(paths.SettingsFile));
        services.AddSingleton<IWorkshopFavoritesStore>(_ => new WorkshopFavoritesStore(paths.WorkshopFavoritesFile,
            warning => Log.Warning("{FavoritesWarning}", warning)));
        services.AddSingleton<IModRepository>(_ => new SqliteModRepository(paths.DatabaseFile));
        services.AddSingleton<ILocalizationService>(_ => new LocalizationService(application.Resources));
        services.AddSingleton<WriteOperationCoordinator>();
        services.AddSingleton<HttpClient>();
        services.AddSingleton<IWorkshopClient, WorkshopClient>();
        services.AddSingleton<IJunctionManager, WindowsJunctionManager>();
        services.AddSingleton<IProcessRunner, ProcessRunner>();
        services.AddSingleton<IFileDeletionService, WindowsFileDeletionService>();
        services.AddSingleton<ILibraryService>(provider => ActivatorUtilities.CreateInstance<LibraryService>(provider, junctionPath));
        services.AddSingleton<ISteamCmdService>(provider => ActivatorUtilities.CreateInstance<SteamCmdService>(provider, paths.SteamCmdDirectory));
        services.AddSingleton<IModOperationService>(provider => ActivatorUtilities.CreateInstance<ModOperationService>(provider, junctionPath));
        services.AddSingleton<IAppUpdateService, AppUpdateService>();
        services.AddSingleton<IPreviewImageService, PreviewImageService>();
        services.AddSingleton<IInstalledWorkshopStateProvider, InstalledWorkshopStateProvider>();
        services.AddSingleton<IModSnapshotService, ModSnapshotService>();
        services.AddSingleton(_ => new ModSnapshotHistory(paths.SnapshotsDirectory));
        services.AddSingleton<DownloadQueueViewModel>();
        services.AddSingleton<WorkshopFavoritesViewModel>();
        services.AddSingleton<MainWindowViewModel>();
        services.AddSingleton<ApplicationUpdateViewModel>(provider => new ApplicationUpdateViewModel(
            provider.GetRequiredService<IAppUpdateService>(),
            provider.GetRequiredService<IWindowFactory>().PrepareForAppUpdateRestartAsync));
        services.AddSingleton<IWindowFactory, WindowFactory>();
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });
    }
}
