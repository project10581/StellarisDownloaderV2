using StellarisDownloader.Core.Models;
using StellarisDownloader.Core.Persistence;

namespace StellarisDownloader.Tests;

public sealed class JsonSettingsStoreTests
{
    [Fact]
    public async Task MissingFileReturnsDefaultsAndRequestsInitialization()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        using var store = new JsonSettingsStore(temporaryDirectory.GetPath("settings.json"));

        var result = await store.LoadAsync();

        Assert.True(result.RequiresInitialization);
        Assert.Null(result.CorruptBackupPath);
        Assert.Equal(AppSettings.CurrentSchemaVersion, result.Settings.SchemaVersion);
        Assert.Null(result.Settings.LibraryRoot);
        Assert.Equal(AppSettings.DefaultLanguage, result.Settings.Language);
        Assert.False(result.Settings.RefreshLibraryOnStartup);
        Assert.False(result.Settings.CheckModUpdatesOnStartup);
        Assert.False(result.Settings.CheckAppUpdatesOnStartup);
    }

    [Fact]
    public async Task SaveWritesAndLoadsTheCompleteSettingsObject()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var settingsPath = temporaryDirectory.GetPath("settings.json");
        using var store = new JsonSettingsStore(settingsPath);
        var expected = new AppSettings
        {
            LibraryRoot = temporaryDirectory.GetPath("library"),
            Language = AppSettings.SimplifiedChineseLanguage,
            RefreshLibraryOnStartup = true,
            CheckModUpdatesOnStartup = true,
            CheckAppUpdatesOnStartup = true,
        };

        await store.SaveAsync(expected);
        var result = await store.LoadAsync();

        Assert.False(result.RequiresInitialization);
        Assert.Equal(expected, result.Settings);
        Assert.Single(Directory.EnumerateFiles(temporaryDirectory.Path));
    }

    [Fact]
    public async Task FailedAtomicReplacementLeavesThePreviousSettingsIntact()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var settingsPath = temporaryDirectory.GetPath("settings.json");
        using var store = new JsonSettingsStore(settingsPath);
        var original = new AppSettings { LibraryRoot = temporaryDirectory.GetPath("original") };
        await store.SaveAsync(original);

        await using (var lockStream = new FileStream(
            settingsPath,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None,
            bufferSize: 1,
            FileOptions.Asynchronous))
        {
            var replacement = original with { Language = AppSettings.SimplifiedChineseLanguage };
            await Assert.ThrowsAnyAsync<IOException>(() => store.SaveAsync(replacement));
        }

        var result = await store.LoadAsync();
        Assert.Equal(original, result.Settings);
        Assert.DoesNotContain(
            Directory.EnumerateFiles(temporaryDirectory.Path),
            path => path.EndsWith(".tmp", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CorruptJsonIsMovedAsideAndInitializationIsRequested()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var settingsPath = temporaryDirectory.GetPath("settings.json");
        await File.WriteAllTextAsync(settingsPath, "{ definitely-not-json");
        using var store = new JsonSettingsStore(settingsPath);

        var result = await store.LoadAsync();

        Assert.True(result.RequiresInitialization);
        Assert.NotNull(result.CorruptBackupPath);
        Assert.False(File.Exists(settingsPath));
        Assert.True(File.Exists(result.CorruptBackupPath));
        Assert.EndsWith(".corrupt", result.CorruptBackupPath, StringComparison.Ordinal);
        Assert.Equal("{ definitely-not-json", await File.ReadAllTextAsync(result.CorruptBackupPath));
    }
}
