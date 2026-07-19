using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using StellarisDownloader.App;
using StellarisDownloader.App.ViewModels;
using StellarisDownloader.Core.Models;
using StellarisDownloader.Core.Persistence;
using StellarisDownloader.Core.Services;

namespace StellarisDownloader.Tests;

public sealed class CompiledXamlTests
{
    [Fact]
    public void MainWindowCompiledXamlLoadsOnAnStaDispatcher()
    {
        WpfTestRunner.Run(() =>
        {
            var settings = new StubSettingsStore(new AppSettings());
            using var repository = new SqliteModRepository(
                Path.Combine(Path.GetTempPath(), $"xaml-{Guid.NewGuid():N}.db"));
            var operations = new UnexpectedModOperationService();
            using var downloadQueue = new DownloadQueueViewModel(
                settings,
                new UnexpectedWorkshopClient(),
                operations);
            using var mainViewModel = new MainWindowViewModel(
                settings,
                repository,
                new StubLibraryService(),
                new StubPreviewImageService(),
                operations,
                downloadQueue);
            using var updateViewModel = new ApplicationUpdateViewModel(
                new UnexpectedAppUpdateService());

            var window = new MainWindow(
                mainViewModel,
                downloadQueue,
                updateViewModel,
                () => throw UnexpectedFactoryCall(),
                () => throw UnexpectedFactoryCall(),
                () => throw UnexpectedFactoryCall(),
                () => throw UnexpectedFactoryCall(),
                () => throw UnexpectedFactoryCall());

            Assert.NotNull(window.Content);
            window.DataContext = null;
            window.Content = null;
            return Task.CompletedTask;
        });
    }

    [Fact]
    public void MainWindowListContextMenuUsesSharedRefreshAndRescanCommands()
    {
        WpfTestRunner.Run(async () =>
        {
            var settings = new StubSettingsStore(new AppSettings());
            using var repository = new SqliteModRepository(
                Path.Combine(Path.GetTempPath(), $"xaml-menu-{Guid.NewGuid():N}.db"));
            var operations = new UnexpectedModOperationService();
            using var downloadQueue = new DownloadQueueViewModel(
                settings,
                new UnexpectedWorkshopClient(),
                operations);
            using var mainViewModel = new MainWindowViewModel(
                settings,
                repository,
                new StubLibraryService(),
                new StubPreviewImageService(),
                operations,
                downloadQueue);
            using var updateViewModel = new ApplicationUpdateViewModel(
                new UnexpectedAppUpdateService());

            var window = new MainWindow(
                mainViewModel,
                downloadQueue,
                updateViewModel,
                () => throw UnexpectedFactoryCall(),
                () => throw UnexpectedFactoryCall(),
                () => throw UnexpectedFactoryCall(),
                () => throw UnexpectedFactoryCall(),
                () => throw UnexpectedFactoryCall());

            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = -32000;
            window.Top = -32000;
            window.ShowInTaskbar = false;
            window.Show();
            await Dispatcher.Yield(DispatcherPriority.Loaded);

            var list = Assert.IsType<ListBox>(window.FindName("ModList"));
            var contextMenu = Assert.IsType<ContextMenu>(list.ContextMenu);
            contextMenu.PlacementTarget = list;
            contextMenu.IsOpen = true;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            contextMenu.GetBindingExpression(FrameworkElement.DataContextProperty)?.UpdateTarget();

            var menuItems = contextMenu.Items.OfType<MenuItem>().ToArray();
            var refreshItem = Assert.Single(
                menuItems,
                item => AutomationProperties.GetAutomationId(item) == "Library.Refresh");
            var rescanItem = Assert.Single(
                menuItems,
                item => AutomationProperties.GetAutomationId(item) == "Library.Rescan");
            refreshItem.GetBindingExpression(MenuItem.CommandProperty)?.UpdateTarget();
            rescanItem.GetBindingExpression(MenuItem.CommandProperty)?.UpdateTarget();

            Assert.Same(mainViewModel, contextMenu.DataContext);
            Assert.Same(mainViewModel.RefreshCommand, refreshItem.Command);
            Assert.Same(mainViewModel.RescanCommand, rescanItem.Command);
            Assert.Equal(mainViewModel.RefreshCommand.CanExecute(null), refreshItem.IsEnabled);
            Assert.Equal(mainViewModel.RescanCommand.CanExecute(null), rescanItem.IsEnabled);

            contextMenu.IsOpen = false;
            window.Close();
        });
    }

    [Fact]
    public void UpdateSelectionWindowRendersReadOnlyApproximateColumn()
    {
        WpfTestRunner.Run(async () =>
        {
            var settings = new StubSettingsStore(new AppSettings
            {
                LibraryRoot = "C:\\Mods",
            });
            var operations = new WindowModOperationService();
            using var viewModel = new UpdateSelectionViewModel(settings, operations);
            await viewModel.CheckUpdatesAsync();
            var window = new UpdateSelectionWindow(viewModel);

            try
            {
                ShowOffscreen(window);
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                window.UpdateLayout();

                var grid = Assert.IsType<DataGrid>(window.FindName("UpdateGrid"));
                var column = Assert.IsType<DataGridCheckBoxColumn>(grid.Columns[5]);
                var binding = Assert.IsType<Binding>(column.Binding);
                Assert.Equal(BindingMode.OneWay, binding.Mode);
                Assert.Single(grid.Items);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void ApplicationUpdateWindowRendersReadOnlyProgress()
    {
        WpfTestRunner.Run(async () =>
        {
            using var viewModel = new ApplicationUpdateViewModel(new WindowAppUpdateService());
            await viewModel.CheckAsync();
            var window = new ApplicationUpdateWindow(viewModel, disposeViewModel: false);

            try
            {
                ShowOffscreen(window);
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                window.UpdateLayout();

                var progressBar = Assert.IsType<ProgressBar>(
                    window.FindName("UpdateProgressBar"));
                var binding = Assert.IsType<Binding>(BindingOperations.GetBinding(
                    progressBar,
                    ProgressBar.ValueProperty));
                Assert.Equal(BindingMode.OneWay, binding.Mode);
                Assert.Equal(100, progressBar.Value);
            }
            finally
            {
                window.Close();
            }
        });
    }

    private static void ShowOffscreen(Window window)
    {
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = -32000;
        window.Top = -32000;
        window.ShowInTaskbar = false;
        window.Show();
    }

    private static InvalidOperationException UnexpectedFactoryCall() =>
        new("The XAML load test must not open child windows.");

    private sealed class UnexpectedWorkshopClient : IWorkshopClient
    {
        public Task<IReadOnlyDictionary<string, WorkshopMetadata>> GetMetadataBatchAsync(
            IReadOnlyCollection<string> workshopIds,
            IProgress<OperationProgress>? progress = null,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The XAML load test must not query Workshop.");
    }

    private sealed class UnexpectedModOperationService : IModOperationService
    {
        public Task<DownloadBatchResult> DownloadBatchAsync(
            IReadOnlyCollection<DownloadRequest> requests,
            IProgress<OperationProgress>? progress = null,
            CancellationToken cancellationToken = default) =>
            throw UnexpectedOperationCall();

        public Task<IReadOnlyList<UpdateCheckResult>> CheckUpdatesAsync(
            string libraryRoot,
            IProgress<OperationProgress>? progress = null,
            CancellationToken cancellationToken = default) =>
            throw UnexpectedOperationCall();

        public Task<DownloadBatchResult> UpdateSelectedAsync(
            string libraryRoot,
            IReadOnlyCollection<string> workshopIds,
            IProgress<OperationProgress>? progress = null,
            CancellationToken cancellationToken = default) =>
            throw UnexpectedOperationCall();

        public Task<DeleteResult> DeleteAsync(
            string libraryRoot,
            string workshopId,
            bool permanently,
            IProgress<OperationProgress>? progress = null,
            CancellationToken cancellationToken = default) =>
            throw UnexpectedOperationCall();

        public Task<RedownloadResult> RedownloadAsync(
            string libraryRoot,
            string workshopId,
            bool permanently,
            IProgress<OperationProgress>? progress = null,
            CancellationToken cancellationToken = default) =>
            throw UnexpectedOperationCall();

        private static InvalidOperationException UnexpectedOperationCall() =>
            new("The XAML load test must not run mod operations.");
    }

    private sealed class UnexpectedAppUpdateService : IAppUpdateService
    {
        public Task<AppUpdateInfo> CheckAsync(
            IProgress<OperationProgress>? progress = null,
            CancellationToken cancellationToken = default) =>
            throw UnexpectedUpdateCall();

        public Task<AppUpdateInfo> DownloadAsync(
            IProgress<OperationProgress>? progress = null,
            CancellationToken cancellationToken = default) =>
            throw UnexpectedUpdateCall();

        public Task ApplyAndRestartAsync(
            IProgress<OperationProgress>? progress = null,
            CancellationToken cancellationToken = default) =>
            throw UnexpectedUpdateCall();

        private static InvalidOperationException UnexpectedUpdateCall() =>
            new("The XAML load test must not contact the update service.");
    }

    private sealed class WindowModOperationService : IModOperationService
    {
        public Task<DownloadBatchResult> DownloadBatchAsync(
            IReadOnlyCollection<DownloadRequest> requests,
            IProgress<OperationProgress>? progress = null,
            CancellationToken cancellationToken = default) =>
            throw UnexpectedCall();

        public Task<IReadOnlyList<UpdateCheckResult>> CheckUpdatesAsync(
            string libraryRoot,
            IProgress<OperationProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<UpdateCheckResult>>(
            [
                new UpdateCheckResult
                {
                    WorkshopId = "123456789",
                    Title = "Approximate imported mod",
                    State = UpdateState.UpdateAvailable,
                    LatestRemoteUpdatedAtUtc = DateTimeOffset.UtcNow,
                    InstalledWorkshopUpdatedAtUtc = DateTimeOffset.UtcNow.AddDays(-1),
                    UsesApproximateLocalTimestamp = true,
                },
            ]);
        }

        public Task<DownloadBatchResult> UpdateSelectedAsync(
            string libraryRoot,
            IReadOnlyCollection<string> workshopIds,
            IProgress<OperationProgress>? progress = null,
            CancellationToken cancellationToken = default) =>
            throw UnexpectedCall();

        public Task<DeleteResult> DeleteAsync(
            string libraryRoot,
            string workshopId,
            bool permanently,
            IProgress<OperationProgress>? progress = null,
            CancellationToken cancellationToken = default) =>
            throw UnexpectedCall();

        public Task<RedownloadResult> RedownloadAsync(
            string libraryRoot,
            string workshopId,
            bool permanently,
            IProgress<OperationProgress>? progress = null,
            CancellationToken cancellationToken = default) =>
            throw UnexpectedCall();

        private static InvalidOperationException UnexpectedCall() =>
            new("The update-window XAML test must only check for mod updates.");
    }

    private sealed class WindowAppUpdateService : IAppUpdateService
    {
        public Task<AppUpdateInfo> CheckAsync(
            IProgress<OperationProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new OperationProgress(
                "CheckingReleaseFeed",
                Completed: 1,
                Total: 1,
                WorkshopId: null,
                Message: "Release feed checked."));
            return Task.FromResult(new AppUpdateInfo
            {
                CurrentVersion = "0.1.0-dev.5",
                LatestVersion = "0.1.0-dev.6",
                ReleaseNotes = "Window binding regression fixture.",
                IsInstalled = true,
                IsUpdateAvailable = true,
            });
        }

        public Task<AppUpdateInfo> DownloadAsync(
            IProgress<OperationProgress>? progress = null,
            CancellationToken cancellationToken = default) =>
            throw UnexpectedCall();

        public Task ApplyAndRestartAsync(
            IProgress<OperationProgress>? progress = null,
            CancellationToken cancellationToken = default) =>
            throw UnexpectedCall();

        private static InvalidOperationException UnexpectedCall() =>
            new("The application-update XAML test must only check for updates.");
    }
}
