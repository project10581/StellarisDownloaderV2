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
}
