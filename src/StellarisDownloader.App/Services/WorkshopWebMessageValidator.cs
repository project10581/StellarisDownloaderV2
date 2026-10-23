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

        if (string.IsNullOrWhiteSpace(json) || json.Length > 65536)
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
        var hasTitle = false;
        string? messageType = null;
        string? title = null;
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
                        || property.Value.GetString() is not (ExpectedMessageType or "addWorkshopFavorite" or "removeWorkshopFavorite"))
                    {
                        return WebBridgeMessageValidationResult.Rejected(
                            WebBridgeMessageError.InvalidMessageType);
                    }

                    messageType = property.Value.GetString();
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

                case "title":
                    if (hasTitle)
                    {
                        return WebBridgeMessageValidationResult.Rejected(WebBridgeMessageError.DuplicateProperty);
                    }
                    hasTitle = true;
                    if (property.Value.ValueKind != JsonValueKind.String || property.Value.GetString()!.Length > 1024)
                    {
                        return WebBridgeMessageValidationResult.Rejected(WebBridgeMessageError.InvalidJson);
                    }
                    title = property.Value.GetString();
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

        if (hasTitle && messageType != "addWorkshopFavorite")
        {
            return WebBridgeMessageValidationResult.Rejected(WebBridgeMessageError.UnknownProperty);
        }
        return ValidateIds(ids, messageType!, title);
    }

    private static WebBridgeMessageValidationResult ValidateIds(JsonElement ids, string messageType, string? title)
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

        if (messageType != ExpectedMessageType && originalCount != 1)
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
            if (string.IsNullOrEmpty(workshopId) || workshopId.Length > 20 || !workshopId.All(IsAsciiDigit))
            {
                return WebBridgeMessageValidationResult.Rejected(
                    WebBridgeMessageError.InvalidWorkshopId);
            }

            if (seenIds.Add(workshopId))
            {
                workshopIds.Add(workshopId);
            }
        }

        return WebBridgeMessageValidationResult.Accepted(workshopIds, messageType, title);
    }

    private static bool IsAsciiDigit(char character) => character is >= '0' and <= '9';
}
