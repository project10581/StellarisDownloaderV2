using StellarisDownloader.App.ViewModels;
using StellarisDownloader.Core.Models;

namespace StellarisDownloader.App.Services;

internal interface IWindowFactory
{
    SettingsWindow CreateSettingsWindow(SettingsLoadResult settings, bool isInitialization);
    MainWindow CreateMainWindow();
    UpdateSelectionViewModel CreateUpdateViewModel();
    UpdateSelectionWindow CreateUpdateWindow(UpdateSelectionViewModel viewModel);
    ApplicationUpdateWindow CreateApplicationUpdateWindow();
    Task PrepareForAppUpdateRestartAsync();
}
