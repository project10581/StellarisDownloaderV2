using StellarisDownloader.Core.Persistence;

namespace StellarisDownloader.Tests;

public sealed class AppDataPathsTests
{
    [Fact]
    public void EnsureDirectoriesCreatesOnlyThePlannedDirectoryLayout()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var root = temporaryDirectory.GetPath("app-data");
        var paths = new AppDataPaths(root);

        paths.EnsureDirectories();

        Assert.True(Directory.Exists(paths.RootDirectory));
        Assert.True(Directory.Exists(paths.SteamCmdDirectory));
        Assert.True(Directory.Exists(paths.CacheDirectory));
        Assert.True(Directory.Exists(paths.PreviewCacheDirectory));
        Assert.True(Directory.Exists(paths.LogsDirectory));
        Assert.Equal(Path.Combine(root, "settings.json"), paths.SettingsFile);
        Assert.Equal(Path.Combine(root, "library.db"), paths.DatabaseFile);
        Assert.Empty(Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories));
    }
}
