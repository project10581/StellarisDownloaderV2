using System.Text.Json;
using StellarisDownloader.App.Models;

namespace StellarisDownloader.App.Services;

public static class WorkshopWebMessageValidator
{
    private const int MaximumIdCount = 100;
    private const string ExpectedMessageType = "enqueueWorkshopIds";

    public static WebBridgeMessageValidationResult Validate(Uri? source, string? json)
    {
        if (!SteamCommunitySecurityPolicy.IsTrustedMessageSource(source))
        {
            return WebBridgeMessageValidationResult.Rejected(
                WebBridgeMessageError.UntrustedSource);
        }

        if (string.IsNullOrWhiteSpace(json))
        {
            return WebBridgeMessageValidationResult.Rejected(WebBridgeMessageError.InvalidJson);
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            return ValidateRoot(document.RootElement);
        }
        catch (JsonException)
        {
            return WebBridgeMessageValidationResult.Rejected(WebBridgeMessageError.InvalidJson);
        }
    }

    private static WebBridgeMessageValidationResult ValidateRoot(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return WebBridgeMessageValidationResult.Rejected(
                WebBridgeMessageError.RootMustBeObject);
        }

        var hasType = false;
        var hasIds = false;
        JsonElement ids = default;

        foreach (var property in root.EnumerateObject())
        {
            switch (property.Name)
            {
                case "type":
                    if (hasType)
                    {
                        return WebBridgeMessageValidationResult.Rejected(
                            WebBridgeMessageError.DuplicateProperty);
                    }

                    hasType = true;
                    if (property.Value.ValueKind != JsonValueKind.String
                        || !string.Equals(
                            property.Value.GetString(),
                            ExpectedMessageType,
                            StringComparison.Ordinal))
                    {
                        return WebBridgeMessageValidationResult.Rejected(
                            WebBridgeMessageError.InvalidMessageType);
                    }

                    break;

                case "ids":
                    if (hasIds)
                    {
                        return WebBridgeMessageValidationResult.Rejected(
                            WebBridgeMessageError.DuplicateProperty);
                    }

                    hasIds = true;
                    ids = property.Value;
                    break;

                default:
                    return WebBridgeMessageValidationResult.Rejected(
                        WebBridgeMessageError.UnknownProperty);
            }
        }

        if (!hasType || !hasIds)
        {
            return WebBridgeMessageValidationResult.Rejected(WebBridgeMessageError.MissingProperty);
        }

        return ValidateIds(ids);
    }

    private static WebBridgeMessageValidationResult ValidateIds(JsonElement ids)
    {
        if (ids.ValueKind != JsonValueKind.Array)
        {
            return WebBridgeMessageValidationResult.Rejected(WebBridgeMessageError.IdsMustBeArray);
        }

        var originalCount = ids.GetArrayLength();
        if (originalCount is < 1 or > MaximumIdCount)
        {
            return WebBridgeMessageValidationResult.Rejected(WebBridgeMessageError.InvalidIdCount);
        }

        var workshopIds = new List<string>(originalCount);
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in ids.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                return WebBridgeMessageValidationResult.Rejected(
                    WebBridgeMessageError.InvalidWorkshopId);
            }

            var workshopId = item.GetString();
            if (string.IsNullOrEmpty(workshopId) || !workshopId.All(IsAsciiDigit))
            {
                return WebBridgeMessageValidationResult.Rejected(
                    WebBridgeMessageError.InvalidWorkshopId);
            }

            if (seenIds.Add(workshopId))
            {
                workshopIds.Add(workshopId);
            }
        }

        return WebBridgeMessageValidationResult.Accepted(workshopIds);
    }

    private static bool IsAsciiDigit(char character) => character is >= '0' and <= '9';
}
