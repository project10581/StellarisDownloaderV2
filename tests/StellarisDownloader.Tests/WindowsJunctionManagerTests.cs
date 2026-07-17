using StellarisDownloader.Core.Integrations;
using StellarisDownloader.Core.Models;

namespace StellarisDownloader.Tests;

public sealed class WindowsJunctionManagerTests
{
    [Fact]
    public async Task SetTargetReplaceAndRestoreNeverDeleteTargetContents()
    {
        Assert.True(OperatingSystem.IsWindows());
        using var temporaryDirectory = new TemporaryDirectory();
        var junctionPath = temporaryDirectory.GetPath(Path.Combine("steamcmd", "281990"));
        var firstTarget = temporaryDirectory.GetPath("first target");
        var secondTarget = temporaryDirectory.GetPath("second target");
        Directory.CreateDirectory(firstTarget);
        Directory.CreateDirectory(secondTarget);
        await File.WriteAllTextAsync(Path.Combine(firstTarget, "first.txt"), "first");
        await File.WriteAllTextAsync(Path.Combine(secondTarget, "second.txt"), "second");
        var manager = new WindowsJunctionManager();

        var firstUpdate = await manager.SetTargetAsync(junctionPath, firstTarget);
        var firstState = await manager.InspectAsync(junctionPath);
        Assert.Equal(JunctionStateKind.Junction, firstState.Kind);
        Assert.Equal(firstTarget, firstState.TargetPath, ignoreCase: true);

        var secondUpdate = await manager.SetTargetAsync(junctionPath, secondTarget);
        Assert.True(File.Exists(Path.Combine(firstTarget, "first.txt")));
        Assert.True(File.Exists(Path.Combine(junctionPath, "second.txt")));

        await manager.RestoreAsync(secondUpdate);
        Assert.True(File.Exists(Path.Combine(junctionPath, "first.txt")));
        Assert.True(File.Exists(Path.Combine(secondTarget, "second.txt")));

        await manager.RestoreAsync(firstUpdate);
        Assert.Equal(JunctionStateKind.Missing, (await manager.InspectAsync(junctionPath)).Kind);
        Assert.True(File.Exists(Path.Combine(firstTarget, "first.txt")));
        Assert.True(File.Exists(Path.Combine(secondTarget, "second.txt")));
    }

    [Fact]
    public async Task OccupiedDirectoryAndFileAreRejectedWithoutDeletion()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var target = temporaryDirectory.GetPath("target");
        Directory.CreateDirectory(target);
        var manager = new WindowsJunctionManager();
        var occupiedDirectory = temporaryDirectory.GetPath("occupied");
        Directory.CreateDirectory(occupiedDirectory);
        var sentinel = Path.Combine(occupiedDirectory, "sentinel.txt");
        await File.WriteAllTextAsync(sentinel, "keep");

        await Assert.ThrowsAsync<IOException>(
            () => manager.SetTargetAsync(occupiedDirectory, target));

        Assert.True(File.Exists(sentinel));
        var blockingFile = temporaryDirectory.GetPath("blocking-file");
        await File.WriteAllTextAsync(blockingFile, "keep");
        await Assert.ThrowsAsync<IOException>(() => manager.SetTargetAsync(blockingFile, target));
        Assert.Equal("keep", await File.ReadAllTextAsync(blockingFile));
    }

    [Fact]
    public async Task EmptyDirectoryCanBeRestoredAfterJunctionReplacement()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var target = temporaryDirectory.GetPath("target");
        var junctionPath = temporaryDirectory.GetPath("empty-placeholder");
        Directory.CreateDirectory(target);
        Directory.CreateDirectory(junctionPath);
        var manager = new WindowsJunctionManager();

        var update = await manager.SetTargetAsync(junctionPath, target);
        await manager.RestoreAsync(update);

        var state = await manager.InspectAsync(junctionPath);
        Assert.Equal(JunctionStateKind.EmptyDirectory, state.Kind);
        Assert.Empty(Directory.EnumerateFileSystemEntries(junctionPath));
    }
}
