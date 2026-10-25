using StellarisDownloader.App.Services;
using StellarisDownloader.App.ViewModels;
using StellarisDownloader.App;
using StellarisDownloader.Core.Models;
using StellarisDownloader.Core.Persistence;
using StellarisDownloader.Core.Services;

namespace StellarisDownloader.Tests;

public sealed class WorkshopFavoritesTests
{
    [Fact]
    public void ClosingTheBrowserWaitsForTheFavoriteWriteBeforeDisposingTheWindow()
    {
        WpfTestRunner.Run(async () =>
        {
            using var directory = new TemporaryDirectory();
            using var store = new WorkshopFavoritesStore(directory.GetPath("favorites.json"));
            var delayed = new DelayedFavoritesStore(store);
            var favorites = new WorkshopFavoritesViewModel(delayed);
            using var queue = new DownloadQueueViewModel(new StubSettingsStore(new AppSettings()),
                new CompiledXamlTests.UnexpectedWorkshopClient(), new CompiledXamlTests.UnexpectedModOperationService());
            var browser = new WorkshopBrowserWindow(queue, new EmptyInstalledState(), favorites);
            var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            browser.Closed += (_, _) => closed.TrySetResult();
            try
            {
                var save = favorites.SetFavoriteAsync("123", true, "Keep after restart");
                Assert.True(favorites.IsBusy);
                Assert.Empty(favorites.Items);
                browser.Close();
                Assert.False(closed.Task.IsCompleted);
                delayed.AllowWrite.SetResult();
                Assert.True(await save);
                await closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
                using var reopened = new WorkshopFavoritesStore(directory.GetPath("favorites.json"));
                Assert.Equal("123", Assert.Single(await reopened.LoadAsync()).WorkshopId);
            }
            finally
            {
                delayed.AllowWrite.TrySetResult();
                await favorites.FlushAsync();
                browser.Close();
            }
        });
    }

    [Fact]
    public async Task FlushWaitsForAllQueuedWritesAndFailedSaveDoesNotClaimSuccess()
    {
        using var directory = new TemporaryDirectory();
        using var store = new WorkshopFavoritesStore(directory.GetPath("favorites.json"));
        var delayed = new DelayedFavoritesStore(store);
        var favorites = new WorkshopFavoritesViewModel(delayed);
        var first = favorites.SetFavoriteAsync("123", true);
        var second = favorites.SetFavoriteAsync("456", true);
        var flush = favorites.FlushAsync();
        Assert.False(flush.IsCompleted);
        delayed.AllowWrite.SetResult();
        await flush;
        Assert.True(await first);
        Assert.True(await second);
        Assert.Equal(2, (await store.LoadAsync()).Count);
        delayed.FailWrites = true;
        Assert.False(await favorites.SetFavoriteAsync("789", true));
        Assert.False(favorites.Contains("789"));
        Assert.NotNull(favorites.LastError);
    }

    private sealed class DelayedFavoritesStore(IWorkshopFavoritesStore inner) : IWorkshopFavoritesStore
    {
        public TaskCompletionSource AllowWrite { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool FailWrites { get; set; }
        public Task<IReadOnlyList<WorkshopFavorite>> LoadAsync(CancellationToken cancellationToken = default) =>
            inner.LoadAsync(cancellationToken);
        public async Task<IReadOnlyList<WorkshopFavorite>> SetFavoriteAsync(
            string workshopId, bool favorite, string? title = null, CancellationToken cancellationToken = default)
        {
            await AllowWrite.Task.WaitAsync(cancellationToken);
            if (FailWrites) { throw new IOException("Disk unavailable."); }
            return await inner.SetFavoriteAsync(workshopId, favorite, title, cancellationToken);
        }
    }

    private sealed class EmptyInstalledState : IInstalledWorkshopStateProvider
    {
        public Task<IReadOnlyList<string>> GetInstalledWorkshopIdsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);
    }

    [Fact]
    public async Task FavoritesPersistWithoutAConfiguredLibraryOrDownloadService()
    {
        using var directory = new TemporaryDirectory();
        var path = directory.GetPath(Path.Combine("appdata", "workshop-favorites.json"));
        using var store = new WorkshopFavoritesStore(path);
        var viewModel = new WorkshopFavoritesViewModel(store);
        await viewModel.LoadAsync();
        Assert.Empty(viewModel.Items);
        await viewModel.SetFavoriteAsync("100", true, "想要但不确定 · 新模组");
        await viewModel.SetFavoriteAsync("100", true, "Duplicate");
        Assert.Null(viewModel.LastError);
        Assert.Equal("想要但不确定 · 新模组", Assert.Single(viewModel.Items).DisplayTitle);

        using var reopened = new WorkshopFavoritesStore(path);
        var saved = Assert.Single(await reopened.LoadAsync());
        Assert.Equal("100", saved.WorkshopId);
        Assert.NotEqual(default, saved.SavedAtUtc);
        await reopened.SetFavoriteAsync("100", false);
        Assert.Empty(await reopened.LoadAsync());
        Assert.Equal(2, Directory.GetFiles(Path.GetDirectoryName(path)!).Length);
        using var backup = new WorkshopFavoritesStore(path + ".bak");
        Assert.Equal("100", Assert.Single(await backup.LoadAsync()).WorkshopId);
    }

    [Fact]
    public async Task ConcurrentChangesAreSerializedAndCancelledSaveDoesNotChangeExistingFile()
    {
        using var directory = new TemporaryDirectory();
        var path = directory.GetPath("favorites.json");
        using var store = new WorkshopFavoritesStore(path);
        await Task.WhenAll(Enumerable.Range(1, 30).Select(id => store.SetFavoriteAsync(id.ToString(System.Globalization.CultureInfo.InvariantCulture), true)));
        Assert.Equal(30, (await store.LoadAsync()).Count);
        var original = await File.ReadAllBytesAsync(path);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            store.SetFavoriteAsync("100", true, cancellationToken: new CancellationToken(true)));
        Assert.Equal(original, await File.ReadAllBytesAsync(path));
    }

    [Theory]
    [InlineData("{")]
    [InlineData("{}")]
    [InlineData("{\"schemaVersion\":2,\"favorites\":[]}")]
    [InlineData("{\"schemaVersion\":1,\"favorites\":[null]}")]
    public async Task UnreadableFavoritesArePreservedAndErrorsSurfaceInTheBrowser(string content)
    {
        using var directory = new TemporaryDirectory();
        var path = directory.GetPath("favorites.json");
        await File.WriteAllTextAsync(path, content);
        using var store = new WorkshopFavoritesStore(path);
        var viewModel = new WorkshopFavoritesViewModel(store);
        await viewModel.LoadAsync();
        Assert.NotNull(viewModel.LastError);
        await viewModel.SetFavoriteAsync("100", true);
        Assert.NotNull(viewModel.LastError);
        Assert.True(viewModel.IsIdle);
        Assert.Equal(content, await File.ReadAllTextAsync(path));
    }

    [Theory]
    [InlineData("addWorkshopFavorite", true)]
    [InlineData("removeWorkshopFavorite", false)]
    public void FavoriteBridgeActionsAreExplicitAndRequireOneNumericId(string type, bool includeTitle)
    {
        var title = includeTitle ? ",\"title\":\"中文名称\"" : "";
        var result = WorkshopWebMessageValidator.Validate(new Uri("https://steamcommunity.com/workshop/"),
            $$"""{"type":"{{type}}","ids":["123"]{{title}}}""");
        Assert.True(result.IsValid);
        Assert.Equal(type, result.MessageType);
        Assert.Equal(includeTitle ? "中文名称" : null, result.Title);
        Assert.False(WorkshopWebMessageValidator.Validate(new Uri("https://steamcommunity.com/workshop/"),
            $$"""{"type":"{{type}}","ids":["123","456"]}""").IsValid);
        Assert.False(WorkshopWebMessageValidator.Validate(new Uri("https://evil.test/"),
            $$"""{"type":"{{type}}","ids":["123"]}""").IsValid);
    }

    [Theory]
    [InlineData("{\"type\":\"addWorkshopFavorite\",\"ids\":[\"../123\"]}")]
    [InlineData("{\"type\":\"addWorkshopFavorite\",\"ids\":[\"123\"],\"title\":\"a\",\"title\":\"b\"}")]
    [InlineData("{\"type\":\"addWorkshopFavorite\",\"ids\":[\"123\"],\"path\":\"C:/Mods\"}")]
    [InlineData("{\"type\":\"enqueueWorkshopIds\",\"ids\":[\"123\"],\"title\":\"a\"}")]
    public void FavoritesDoNotBroadenTheBrowserBridgeToArbitraryCommands(string json)
    {
        Assert.False(WorkshopWebMessageValidator.Validate(new Uri("https://steamcommunity.com/"), json).IsValid);
    }
}
