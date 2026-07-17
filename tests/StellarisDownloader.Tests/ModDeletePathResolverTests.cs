using StellarisDownloader.Core.Integrations;
using StellarisDownloader.Core.Services;

namespace StellarisDownloader.Tests;

public sealed class ModDeletePathResolverTests
{
    [Fact]
    public void DirectNumericChildIsTheOnlyResolvedTarget()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var root = temporaryDirectory.GetPath("library");
        var target = Path.Combine(root, "123456789");
        Directory.CreateDirectory(target);

        var result = ModDeletePathResolver.Resolve(root, "123456789");

        Assert.Equal(target, result, ignoreCase: true);
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("not-numeric")]
    [InlineData("１２３")]
    public void NonAsciiNumericOrTraversalIdsAreRejected(string workshopId)
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var root = temporaryDirectory.GetPath("library");
        Directory.CreateDirectory(root);

        Assert.Throws<ArgumentException>(() => ModDeletePathResolver.Resolve(root, workshopId));
    }

    [Fact]
    public void AFileAtTheExpectedPathIsRejected()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var root = temporaryDirectory.GetPath("library");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "123"), "not a directory");

        Assert.Throws<InvalidOperationException>(() => ModDeletePathResolver.Resolve(root, "123"));
    }

    [Fact]
    public async Task ReparsePointModFolderIsRejectedWithoutTouchingItsTarget()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var root = temporaryDirectory.GetPath("library");
        var target = temporaryDirectory.GetPath("external-target");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(target);
        var sentinel = Path.Combine(target, "sentinel.txt");
        await File.WriteAllTextAsync(sentinel, "keep");
        var junctionPath = Path.Combine(root, "123");
        var manager = new WindowsJunctionManager();
        var update = await manager.SetTargetAsync(junctionPath, target);

        try
        {
            Assert.Throws<InvalidOperationException>(
                () => ModDeletePathResolver.Resolve(root, "123"));
            Assert.True(File.Exists(sentinel));
        }
        finally
        {
            await manager.RestoreAsync(update, CancellationToken.None);
        }
    }
}
