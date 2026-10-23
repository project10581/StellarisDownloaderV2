using StellarisDownloader.App.Services;
using StellarisDownloader.App.ViewModels;
using StellarisDownloader.Core.Persistence;

namespace StellarisDownloader.Tests;

public sealed class WorkshopFavoritesTests
{
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
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(path)!));
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
