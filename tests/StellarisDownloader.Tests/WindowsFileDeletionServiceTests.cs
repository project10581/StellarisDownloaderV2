using StellarisDownloader.Core.Integrations;

namespace StellarisDownloader.Tests;

public sealed class WindowsFileDeletionServiceTests
{
    [Fact]
    public async Task PermanentDeleteRemovesOnlyTheExplicitDirectory()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var target = temporaryDirectory.GetPath("123");
        var sibling = temporaryDirectory.GetPath("sibling");
        Directory.CreateDirectory(target);
        Directory.CreateDirectory(sibling);
        await File.WriteAllTextAsync(Path.Combine(target, "content.txt"), "delete");
        await File.WriteAllTextAsync(Path.Combine(sibling, "keep.txt"), "keep");
        var service = new WindowsFileDeletionService();

        await service.DeleteDirectoryPermanentlyAsync(target);

        Assert.False(Directory.Exists(target));
        Assert.True(File.Exists(Path.Combine(sibling, "keep.txt")));
    }

    [Fact]
    public async Task PermanentDeleteRejectsAReparsePointWithoutTouchingItsTarget()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var target = temporaryDirectory.GetPath("external-target");
        Directory.CreateDirectory(target);
        var sentinel = Path.Combine(target, "sentinel.txt");
        await File.WriteAllTextAsync(sentinel, "keep");
        var junctionPath = temporaryDirectory.GetPath("123");
        var manager = new WindowsJunctionManager();
        var update = await manager.SetTargetAsync(junctionPath, target);
        var service = new WindowsFileDeletionService();

        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => service.DeleteDirectoryPermanentlyAsync(junctionPath));
            Assert.True(File.Exists(sentinel));
        }
        finally
        {
            await manager.RestoreAsync(update, CancellationToken.None);
        }
    }
}
