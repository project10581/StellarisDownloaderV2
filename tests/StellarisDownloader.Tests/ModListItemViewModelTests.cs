using System.Globalization;
using StellarisDownloader.App.ViewModels;
using StellarisDownloader.Core.Models;

namespace StellarisDownloader.Tests;

public sealed class ModListItemViewModelTests
{
    [Theory]
    [InlineData(null, "—")]
    [InlineData(0L, "0 B")]
    [InlineData(1023L, "1,023 B")]
    [InlineData(1024L, "1 KB")]
    [InlineData(1536L, "1.5 KB")]
    [InlineData(1416269L, "1.35 MB")]
    [InlineData(2147483648L, "2 GB")]
    public void FileSizeUsesHumanReadableBinaryUnits(long? fileSize, string expected)
    {
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            var item = new ModListItemViewModel(CreateRecord(fileSize: fileSize));

            Assert.Equal(expected, item.DisplayFileSize);
            Assert.Equal(fileSize ?? 0, item.FileSize);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    [Theory]
    [InlineData("76561198000000001", "Real Author", "Real Author")]
    [InlineData("76561198000000001", null, "76561198000000001")]
    [InlineData(null, null, "—")]
    public void AuthorPrefersDisplayNameThenSteamId(
        string? creatorId,
        string? creatorName,
        string expected)
    {
        var item = new ModListItemViewModel(CreateRecord() with
        {
            CreatorId = creatorId,
            CreatorName = creatorName,
        });

        Assert.Equal(expected, item.DisplayAuthor);
    }

    private static ModRecord CreateRecord(long? fileSize = null) => new()
    {
        WorkshopId = "123456789",
        ContentPath = "C:\\Mods\\123456789",
        FileSize = fileSize,
        ImportedOrDownloadedAtUtc = DateTimeOffset.UnixEpoch,
        LastScannedAtUtc = DateTimeOffset.UnixEpoch,
    };
}
