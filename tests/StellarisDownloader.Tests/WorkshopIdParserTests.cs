using StellarisDownloader.Core.Services;

namespace StellarisDownloader.Tests;

public sealed class WorkshopIdParserTests
{
    [Theory]
    [InlineData("123456789", "123456789")]
    [InlineData(" https://steamcommunity.com/sharedfiles/filedetails/?id=234567890 ", "234567890")]
    [InlineData("http://www.steamcommunity.com/workshop/filedetails/?search=x&id=345678901", "345678901")]
    [InlineData("https://steamcommunity.com/sharedfiles/filedetails/?id=123%34", "1234")]
    public void SupportedIdAndUrlFormatsAreParsed(string input, string expectedId)
    {
        var parsed = WorkshopIdParser.TryParse(input, out var workshopId, out var error);

        Assert.True(parsed, error);
        Assert.Equal(expectedId, workshopId);
    }

    [Theory]
    [InlineData("mod 123456789")]
    [InlineData("１２３４５６")]
    [InlineData("file:///C:/private/123")]
    [InlineData("https://example.com/?id=123")]
    [InlineData("https://steamcommunity.com.evil.test/?id=123")]
    [InlineData("https://steamcommunity.com/?id=abc")]
    [InlineData("https://steamcommunity.com/?id=123&id=456")]
    public void AmbiguousOrUntrustedInputsAreRejected(string input)
    {
        Assert.False(WorkshopIdParser.TryParse(input, out _, out var error));
        Assert.NotEmpty(error);
    }

    [Fact]
    public void MultilineParsingKeepsValidItemsDeduplicatedAndReportsInvalidLines()
    {
        const string Input = """
            100
            invalid
            https://steamcommunity.com/sharedfiles/filedetails/?id=200
            100
            https://evil.test/?id=300
            """;

        var result = WorkshopIdParser.Parse(Input);

        Assert.Equal(["100", "200"], result.WorkshopIds);
        Assert.Equal(2, result.InvalidInputs.Count);
        Assert.Equal("invalid", result.InvalidInputs[0].Input);
        Assert.Equal("https://evil.test/?id=300", result.InvalidInputs[1].Input);
    }

    [Fact]
    public void TrustedHostCheckDoesNotAcceptLookalikeSuffixes()
    {
        Assert.True(WorkshopIdParser.IsTrustedSteamCommunityHost("steamcommunity.com"));
        Assert.True(WorkshopIdParser.IsTrustedSteamCommunityHost("www.steamcommunity.com"));
        Assert.False(WorkshopIdParser.IsTrustedSteamCommunityHost("steamcommunity.com.evil.test"));
        Assert.False(WorkshopIdParser.IsTrustedSteamCommunityHost("notsteamcommunity.com"));
    }
}
