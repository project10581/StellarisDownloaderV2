using System.Globalization;
using System.Text.Json;
using StellarisDownloader.App.Models;
using StellarisDownloader.App.Services;

namespace StellarisDownloader.Tests;

public sealed class WorkshopWebMessageValidatorTests
{
    private static readonly Uri TrustedSource =
        new("https://steamcommunity.com/sharedfiles/filedetails/?id=123");

    [Fact]
    public void ValidMessageDeduplicatesIdsWhilePreservingOrder()
    {
        const string Message = """
            {"type":"enqueueWorkshopIds","ids":["200","100","200","300"]}
            """;

        var result = WorkshopWebMessageValidator.Validate(TrustedSource, Message);

        Assert.True(result.IsValid);
        Assert.Equal(WebBridgeMessageError.None, result.Error);
        Assert.Equal(["200", "100", "300"], result.WorkshopIds);
    }

    [Theory]
    [InlineData("http://steamcommunity.com/")]
    [InlineData("https://steamcommunity.com.evil.test/")]
    [InlineData("https://user@steamcommunity.com/")]
    [InlineData("https://@steamcommunity.com/")]
    [InlineData("about:blank")]
    public void UntrustedSourceIsRejectedBeforeMessageUse(string source)
    {
        var result = WorkshopWebMessageValidator.Validate(
            new Uri(source),
            """{"type":"enqueueWorkshopIds","ids":["123"]}""");

        AssertRejected(result, WebBridgeMessageError.UntrustedSource);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("\"message\"")]
    public void RootMustBeAnObject(string message)
    {
        var result = WorkshopWebMessageValidator.Validate(TrustedSource, message);

        AssertRejected(result, WebBridgeMessageError.RootMustBeObject);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("{/*comment*/\"type\":\"enqueueWorkshopIds\",\"ids\":[\"1\"]}")]
    [InlineData("{\"type\":\"enqueueWorkshopIds\",\"ids\":[\"1\"],}")]
    public void InvalidJsonIsRejected(string message)
    {
        var result = WorkshopWebMessageValidator.Validate(TrustedSource, message);

        AssertRejected(result, WebBridgeMessageError.InvalidJson);
    }

    [Theory]
    [InlineData("{\"type\":\"enqueueWorkshopIds\"}")]
    [InlineData("{\"ids\":[\"123\"]}")]
    public void BothRequiredPropertiesMustBePresent(string message)
    {
        var result = WorkshopWebMessageValidator.Validate(TrustedSource, message);

        AssertRejected(result, WebBridgeMessageError.MissingProperty);
    }

    [Theory]
    [InlineData("{\"type\":\"enqueueWorkshopIds\",\"ids\":[\"123\"],\"path\":\"C:/private\"}")]
    [InlineData("{\"Type\":\"enqueueWorkshopIds\",\"ids\":[\"123\"]}")]
    public void UnknownOrIncorrectlyCasedPropertiesAreRejected(string message)
    {
        var result = WorkshopWebMessageValidator.Validate(TrustedSource, message);

        AssertRejected(result, WebBridgeMessageError.UnknownProperty);
    }

    [Theory]
    [InlineData("{\"type\":\"enqueueWorkshopIds\",\"type\":\"enqueueWorkshopIds\",\"ids\":[\"123\"]}")]
    [InlineData("{\"type\":\"enqueueWorkshopIds\",\"ids\":[\"123\"],\"ids\":[\"456\"]}")]
    public void DuplicatePropertiesAreRejected(string message)
    {
        var result = WorkshopWebMessageValidator.Validate(TrustedSource, message);

        AssertRejected(result, WebBridgeMessageError.DuplicateProperty);
    }

    [Theory]
    [InlineData("{\"type\":\"other\",\"ids\":[\"123\"]}")]
    [InlineData("{\"type\":null,\"ids\":[\"123\"]}")]
    [InlineData("{\"type\":1,\"ids\":[\"123\"]}")]
    public void OnlyTheExpectedMessageTypeIsAccepted(string message)
    {
        var result = WorkshopWebMessageValidator.Validate(TrustedSource, message);

        AssertRejected(result, WebBridgeMessageError.InvalidMessageType);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("\"123\"")]
    [InlineData("123")]
    [InlineData("{}")]
    public void IdsMustBeAnArray(string idsJson)
    {
        var message = $$"""{"type":"enqueueWorkshopIds","ids":{{idsJson}}}""";

        var result = WorkshopWebMessageValidator.Validate(TrustedSource, message);

        AssertRejected(result, WebBridgeMessageError.IdsMustBeArray);
    }

    [Fact]
    public void EmptyIdArrayIsRejected()
    {
        var result = WorkshopWebMessageValidator.Validate(
            TrustedSource,
            """{"type":"enqueueWorkshopIds","ids":[]}""");

        AssertRejected(result, WebBridgeMessageError.InvalidIdCount);
    }

    [Fact]
    public void RawArrayLengthIsLimitedBeforeDeduplication()
    {
        var ids = Enumerable.Repeat("123", 101).ToArray();
        var message = JsonSerializer.Serialize(new
        {
            type = "enqueueWorkshopIds",
            ids,
        });

        var result = WorkshopWebMessageValidator.Validate(TrustedSource, message);

        AssertRejected(result, WebBridgeMessageError.InvalidIdCount);
    }

    [Fact]
    public void OneHundredIdsAreAccepted()
    {
        var ids = Enumerable.Range(1, 100)
            .Select(value => value.ToString(CultureInfo.InvariantCulture))
            .ToArray();
        var message = JsonSerializer.Serialize(new
        {
            type = "enqueueWorkshopIds",
            ids,
        });

        var result = WorkshopWebMessageValidator.Validate(TrustedSource, message);

        Assert.True(result.IsValid);
        Assert.Equal(ids, result.WorkshopIds);
    }

    [Theory]
    [InlineData("\"\"")]
    [InlineData("\"123abc\"")]
    [InlineData("\"１２３\"")]
    [InlineData("123")]
    [InlineData("null")]
    [InlineData("{}")]
    public void EveryIdMustBeAnAsciiDigitString(string idJson)
    {
        var message = $$"""{"type":"enqueueWorkshopIds","ids":[{{idJson}}]}""";

        var result = WorkshopWebMessageValidator.Validate(TrustedSource, message);

        AssertRejected(result, WebBridgeMessageError.InvalidWorkshopId);
    }

    private static void AssertRejected(
        WebBridgeMessageValidationResult result,
        WebBridgeMessageError expectedError)
    {
        Assert.False(result.IsValid);
        Assert.Equal(expectedError, result.Error);
        Assert.Empty(result.WorkshopIds);
    }
}
