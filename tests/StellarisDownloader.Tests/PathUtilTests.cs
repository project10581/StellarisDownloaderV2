using StellarisDownloader.Core.Services;

namespace StellarisDownloader.Tests;

public sealed class PathUtilTests
{
    [Theory]
    [InlineData("C:\\Mods\\", "C:\\Mods")]
    [InlineData("C:/Mods/", "C:\\Mods")]
    [InlineData("C:\\", "C:\\")]
    public void NormalizeTrimsTrailingSeparatorsButPreservesRoots(string path, string expected) =>
        Assert.Equal(expected, PathUtil.Normalize(path));

    [Fact]
    public void RelativePathsAreResolvedAndComparisonIgnoresCaseAndSeparators()
    {
        var relative = Path.Combine("mods", "..", "Library");
        var full = Path.Combine(Environment.CurrentDirectory, "Library");
        Assert.Equal(full, PathUtil.Normalize(relative));
        Assert.True(PathUtil.Equal(relative, full.ToUpperInvariant() + Path.DirectorySeparatorChar));
        Assert.False(PathUtil.Equal(full, full + "Other"));
        Assert.False(PathUtil.Equal(null, null));
        Assert.False(PathUtil.Equal(full, null));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void NormalizeRejectsMissingPaths(string? path) =>
        Assert.ThrowsAny<ArgumentException>(() => PathUtil.Normalize(path!));
}
