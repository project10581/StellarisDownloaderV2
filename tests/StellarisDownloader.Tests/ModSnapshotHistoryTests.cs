using StellarisDownloader.Core.Persistence;

namespace StellarisDownloader.Tests;

public sealed class ModSnapshotHistoryTests
{
    [Fact]
    public async Task ConcurrentSavesWithIdenticalTimestampsCreateSeparateCompleteVersions()
    {
        using var temporary = new TemporaryDirectory();
        var history = new ModSnapshotHistory(temporary.GetPath("snapshots"));
        var snapshot = ModSnapshotTests.Snapshot("100");

        var paths = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => history.SaveAsync(snapshot)));

        Assert.Equal(8, paths.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        foreach (var path in paths)
        {
            Assert.Equal("100", Assert.Single((await ModSnapshotFile.ReadAsync(path)).Mods).WorkshopId);
            Assert.Equal(history.DirectoryPath, Path.GetDirectoryName(path));
        }

        Assert.Empty(Directory.EnumerateFiles(history.DirectoryPath, "*.tmp"));
        Assert.Equal(8, (await new ModSnapshotHistory(history.DirectoryPath).ListAsync()).Entries.Count);
    }

    [Fact]
    public async Task HistoryIsNewestFirstAndUnreadableFilesAreReportedWithoutRemovingThem()
    {
        using var temporary = new TemporaryDirectory();
        var history = new ModSnapshotHistory(temporary.GetPath("snapshots"));
        var old = ModSnapshotTests.Snapshot("100");
        var oldPath = await history.SaveAsync(old);
        var newPath = await history.SaveAsync(old with { CapturedAtUtc = old.CapturedAtUtc.AddDays(1) });
        var corruptPath = Path.Combine(history.DirectoryPath, "broken.json");
        await File.WriteAllTextAsync(corruptPath, "broken");
        Directory.CreateDirectory(history.ExportsDirectory);
        await ModSnapshotFile.WriteAsync(Path.Combine(history.ExportsDirectory, "copy.json"), old);

        var result = await history.ListAsync();

        Assert.Equal([newPath, oldPath], result.Entries.Select(entry => entry.FilePath));
        Assert.Equal(1, result.UnreadableCount);
        Assert.Equal("broken", await File.ReadAllTextAsync(corruptPath));
        Assert.True(history.IsManagedVersionPath(newPath));
        Assert.False(history.IsManagedVersionPath(Path.Combine(history.ExportsDirectory, "copy.json")));
    }

    [Fact]
    public async Task NewVersionWriterNeverReplacesExistingFiles()
    {
        using var temporary = new TemporaryDirectory();
        var path = temporary.GetPath("existing.json");
        await ModSnapshotFile.WriteNewAsync(path, ModSnapshotTests.Snapshot("100"));

        await Assert.ThrowsAnyAsync<IOException>(() => ModSnapshotFile.WriteNewAsync(path, ModSnapshotTests.Snapshot("200")));

        Assert.Equal("100", Assert.Single((await ModSnapshotFile.ReadAsync(path)).Mods).WorkshopId);
        Assert.Empty(Directory.EnumerateFiles(temporary.Path, "*.tmp"));
    }

    [Fact]
    public async Task CancelledSavesLeaveExistingHistoryUnchanged()
    {
        using var temporary = new TemporaryDirectory();
        var history = new ModSnapshotHistory(temporary.GetPath("snapshots"));
        var oldPath = await history.SaveAsync(ModSnapshotTests.Snapshot("100"));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => history.SaveAsync(ModSnapshotTests.Snapshot("200"), cancellation.Token));

        Assert.Equal(oldPath, Assert.Single((await history.ListAsync()).Entries).FilePath);
    }
}
