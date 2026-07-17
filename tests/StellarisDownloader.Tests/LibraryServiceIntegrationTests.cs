using StellarisDownloader.Core.Integrations;
using StellarisDownloader.Core.Models;
using StellarisDownloader.Core.Persistence;
using StellarisDownloader.Core.Services;

namespace StellarisDownloader.Tests;

public sealed class LibraryServiceIntegrationTests
{
    [Fact]
    public async Task RealSettingsDatabaseAndJunctionCompleteALibrarySwitchTogether()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var oldRoot = temporaryDirectory.GetPath("old library");
        var newRoot = temporaryDirectory.GetPath("new library");
        await CreateModAsync(oldRoot, "100");
        await CreateModAsync(newRoot, "200");
        var settingsPath = temporaryDirectory.GetPath("settings.json");
        using var settingsStore = new JsonSettingsStore(settingsPath);
        await settingsStore.SaveAsync(new AppSettings { LibraryRoot = oldRoot });
        using var repository = new SqliteModRepository(temporaryDirectory.GetPath("library.db"));
        await repository.InitializeAsync();
        var timestamp = DateTimeOffset.UtcNow;
        await repository.ReplaceSnapshotAsync(
            oldRoot,
            [CreateRecord(oldRoot, "100", timestamp)],
            timestamp);
        var junctionPath = temporaryDirectory.GetPath(
            Path.Combine("steamcmd", "steamapps", "workshop", "content", "281990"));
        var junctionManager = new WindowsJunctionManager();
        var initialJunction = await junctionManager.SetTargetAsync(junctionPath, oldRoot);
        using var coordinator = new WriteOperationCoordinator();
        var service = new LibraryService(
            settingsStore,
            repository,
            junctionManager,
            coordinator,
            junctionPath);

        try
        {
            var result = await service.SwitchAsync(new AppSettings
            {
                LibraryRoot = newRoot,
                Language = AppSettings.SimplifiedChineseLanguage,
                RefreshLibraryOnStartup = true,
            });

            Assert.Equal(OperationStatus.Succeeded, result.Status);
            var savedSettings = await settingsStore.LoadAsync();
            Assert.Equal(newRoot, savedSettings.Settings.LibraryRoot);
            Assert.Equal(AppSettings.SimplifiedChineseLanguage, savedSettings.Settings.Language);
            Assert.True(savedSettings.Settings.RefreshLibraryOnStartup);
            Assert.Equal(
                newRoot,
                (await junctionManager.InspectAsync(junctionPath)).TargetPath,
                ignoreCase: true);
            Assert.True(File.Exists(Path.Combine(oldRoot, "100", "content.txt")));
            Assert.True(File.Exists(Path.Combine(newRoot, "200", "content.txt")));
            Assert.Equal(["200"], (await repository.ListAsync(newRoot)).Select(record => record.WorkshopId));
        }
        finally
        {
            await junctionManager.RestoreAsync(initialJunction, CancellationToken.None);
        }
    }

    private static async Task CreateModAsync(string root, string workshopId)
    {
        var path = Path.Combine(root, workshopId);
        Directory.CreateDirectory(path);
        await File.WriteAllTextAsync(Path.Combine(path, "content.txt"), "content");
    }

    private static ModRecord CreateRecord(
        string root,
        string workshopId,
        DateTimeOffset timestamp)
    {
        return new ModRecord
        {
            WorkshopId = workshopId,
            ContentPath = Path.Combine(root, workshopId),
            ImportedOrDownloadedAtUtc = timestamp,
            LastScannedAtUtc = timestamp,
        };
    }
}
