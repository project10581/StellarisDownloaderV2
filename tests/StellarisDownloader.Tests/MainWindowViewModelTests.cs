using System.Windows.Media;
using StellarisDownloader.App.ViewModels;
using StellarisDownloader.Core.Models;
using StellarisDownloader.Core.Persistence;

namespace StellarisDownloader.Tests;

public sealed class MainWindowViewModelTests
{
    private static readonly DateTimeOffset TestTime = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

    [Fact]
    public async Task SearchMatchesTitleAndWorkshopIdWhileListItemsDisplayOnlyTitles()
    {
        using var fixture = await MainWindowFixture.CreateAsync(CreateRecords());
        await fixture.ViewModel.InitializeAsync();
        AssertReady(fixture.ViewModel);

        fixture.ViewModel.SearchText = "200";
        var result = Assert.Single(VisibleItems(fixture.ViewModel));

        Assert.Equal("Bravo", result.DisplayTitle);
        Assert.DoesNotContain(result.WorkshopId, result.DisplayTitle, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FourSortOptionsProduceTheExpectedOrders()
    {
        using var fixture = await MainWindowFixture.CreateAsync(CreateRecords());
        await fixture.ViewModel.InitializeAsync();
        AssertReady(fixture.ViewModel);

        AssertOrder(fixture.ViewModel, ModSortOption.Title, "100", "200", "300");
        AssertOrder(fixture.ViewModel, ModSortOption.RemoteUpdated, "200", "300", "100");
        AssertOrder(fixture.ViewModel, ModSortOption.LastDownloaded, "100", "300", "200");
        AssertOrder(fixture.ViewModel, ModSortOption.FileSize, "200", "300", "100");
    }

    [Fact]
    public async Task SelectionChangesOnlyTheSelectedDetailRecord()
    {
        using var fixture = await MainWindowFixture.CreateAsync(CreateRecords());
        await fixture.ViewModel.InitializeAsync();
        AssertReady(fixture.ViewModel);
        var selected = VisibleItems(fixture.ViewModel)[1];

        fixture.ViewModel.SelectedMod = selected;

        Assert.True(fixture.ViewModel.HasSelection);
        Assert.Same(selected, fixture.ViewModel.SelectedMod);
        Assert.Equal("200", fixture.ViewModel.SelectedMod.Record.WorkshopId);
        Assert.Equal(3, VisibleItems(fixture.ViewModel).Count);
    }

    [Fact]
    public async Task StaleCacheClearsRowsAndDisablesLibraryWrites()
    {
        using var fixture = await MainWindowFixture.CreateAsync(CreateRecords());
        await fixture.ViewModel.InitializeAsync();
        await fixture.Repository.MarkCacheStaleAsync();

        await fixture.ViewModel.RefreshAsync();

        Assert.Empty(VisibleItems(fixture.ViewModel));
        Assert.Equal(LibraryViewState.Stale, fixture.ViewModel.State);
        Assert.False(fixture.ViewModel.CanModifyLibrary);
        Assert.False(fixture.ViewModel.CanRunWriteOperations);
        Assert.False(fixture.ViewModel.RescanCommand.CanExecute(null));
    }

    [Fact]
    public void RunningWriteOperationDisablesConflictingCommandsUntilCompletion()
    {
        WpfTestRunner.Run(async () =>
        {
            using var fixture = await MainWindowFixture.CreateAsync(CreateRecords());
            await fixture.ViewModel.InitializeAsync();
            AssertReady(fixture.ViewModel);
            var completion = new TaskCompletionSource<LibraryScanResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            fixture.Library.PendingScan = completion;

            var scanTask = fixture.ViewModel.RescanAsync();

            Assert.True(fixture.ViewModel.IsBusy);
            Assert.False(fixture.ViewModel.CanRunWriteOperations);
            Assert.False(fixture.ViewModel.RescanCommand.CanExecute(null));

            completion.SetResult(new LibraryScanResult
            {
                Status = OperationStatus.Succeeded,
                LibraryRoot = fixture.LibraryRoot,
                Records = CreateRecords(),
            });
            await scanTask;

            Assert.False(fixture.ViewModel.IsBusy);
            Assert.True(fixture.ViewModel.CanRunWriteOperations);
        });
    }

    [Fact]
    public async Task ANewSelectionPreventsAnOlderPreviewRequestFromOverwritingIt()
    {
        var preview = new StubPreviewImageService();
        var firstResponse = preview.EnqueueResponse();
        var secondResponse = preview.EnqueueResponse();
        using var fixture = await MainWindowFixture.CreateAsync(CreateRecords(withPreviews: true), preview);
        await fixture.ViewModel.InitializeAsync();
        AssertReady(fixture.ViewModel);
        var items = VisibleItems(fixture.ViewModel);
        var firstImage = new DrawingImage();
        firstImage.Freeze();
        var secondImage = new DrawingImage();
        secondImage.Freeze();

        fixture.ViewModel.SelectedMod = items[0];
        fixture.ViewModel.SelectedMod = items[1];
        secondResponse.SetResult(secondImage);
        await WaitUntilAsync(() => ReferenceEquals(fixture.ViewModel.PreviewImage, secondImage));
        firstResponse.SetResult(firstImage);
        await Task.Delay(50);

        Assert.Same(secondImage, fixture.ViewModel.PreviewImage);
        Assert.Equal(2, preview.CallCount);
    }

    private static IReadOnlyList<ModRecord> CreateRecords(bool withPreviews = false) =>
    [
        CreateRecord("100", "Alpha", 100, remoteOffset: 1, downloadOffset: 3, withPreviews),
        CreateRecord("200", "Bravo", 300, remoteOffset: 3, downloadOffset: 1, withPreviews),
        CreateRecord("300", "Charlie", 200, remoteOffset: 2, downloadOffset: 2, withPreviews),
    ];

    private static ModRecord CreateRecord(
        string id,
        string title,
        long size,
        int remoteOffset,
        int downloadOffset,
        bool withPreview) => new()
        {
            WorkshopId = id,
            Title = title,
            PreviewUrl = withPreview ? $"https://example.test/{id}.png" : null,
            ContentPath = Path.Combine("C:\\Mods", id),
            FileSize = size,
            ImportedOrDownloadedAtUtc = TestTime.AddHours(downloadOffset),
            InstalledWorkshopUpdatedAtUtc = TestTime.AddHours(remoteOffset),
            LastScannedAtUtc = TestTime,
        };

    private static List<ModListItemViewModel> VisibleItems(MainWindowViewModel viewModel) =>
        viewModel.ModsView.Cast<ModListItemViewModel>().ToList();

    private static void AssertOrder(
        MainWindowViewModel viewModel,
        ModSortOption sort,
        params string[] expectedIds)
    {
        viewModel.SelectedSort = sort;
        Assert.Equal(expectedIds, VisibleItems(viewModel).Select(item => item.WorkshopId));
    }

    private static void AssertReady(MainWindowViewModel viewModel) =>
        Assert.True(
            viewModel.State == LibraryViewState.Ready,
            $"Expected a ready library, but state was {viewModel.State}: {viewModel.LastError}");

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var timeout = DateTime.UtcNow.AddSeconds(2);
        while (!condition())
        {
            if (DateTime.UtcNow >= timeout)
            {
                throw new TimeoutException("The expected view-model state was not reached.");
            }

            await Task.Delay(10);
        }
    }

    private sealed class MainWindowFixture : IDisposable
    {
        private readonly TemporaryDirectory temporaryDirectory;

        private MainWindowFixture(
            TemporaryDirectory temporaryDirectory,
            SqliteModRepository repository,
            StubLibraryService library,
            MainWindowViewModel viewModel,
            string libraryRoot)
        {
            this.temporaryDirectory = temporaryDirectory;
            Repository = repository;
            Library = library;
            ViewModel = viewModel;
            LibraryRoot = libraryRoot;
        }

        public SqliteModRepository Repository { get; }

        public StubLibraryService Library { get; }

        public MainWindowViewModel ViewModel { get; }

        public string LibraryRoot { get; }

        public static async Task<MainWindowFixture> CreateAsync(
            IReadOnlyCollection<ModRecord> records,
            StubPreviewImageService? preview = null)
        {
            var temporaryDirectory = new TemporaryDirectory();
            var root = temporaryDirectory.GetPath("library");
            Directory.CreateDirectory(root);
            var repository = new SqliteModRepository(temporaryDirectory.GetPath("library.db"));
            await repository.InitializeAsync();
            await repository.ReplaceSnapshotAsync(root, records, TestTime);
            var settings = new StubSettingsStore(new AppSettings { LibraryRoot = root });
            var library = new StubLibraryService();
            var viewModel = new MainWindowViewModel(
                settings,
                repository,
                library,
                preview ?? new StubPreviewImageService());
            return new MainWindowFixture(temporaryDirectory, repository, library, viewModel, root);
        }

        public void Dispose()
        {
            ViewModel.Dispose();
            Repository.Dispose();
            temporaryDirectory.Dispose();
        }
    }
}
