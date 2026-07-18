using StellarisDownloader.Core.Models;

namespace StellarisDownloader.Core.Services;

public static class WorkshopIdParser
{
    public static WorkshopInputParseResult Parse(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return new WorkshopInputParseResult([], []);
        }

        var workshopIds = new List<string>();
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        var invalidInputs = new List<InvalidWorkshopInput>();
        var lines = input.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');

        for (var index = 0; index < lines.Length; index++)
        {
            var candidate = lines[index].Trim();
            if (candidate.Length == 0)
            {
                continue;
            }

            if (!TryParse(candidate, out var workshopId, out var error))
            {
                invalidInputs.Add(new InvalidWorkshopInput(index + 1, candidate, error));
                continue;
            }

            if (seenIds.Add(workshopId))
            {
                workshopIds.Add(workshopId);
            }
        }

        return new WorkshopInputParseResult(workshopIds, invalidInputs);
    }

    public static bool TryParse(
        string input,
        out string workshopId,
        out string error)
    {
        workshopId = string.Empty;
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(input))
        {
            error = "Workshop input is empty.";
            return false;
        }

        var candidate = input.Trim();
        if (IsAsciiDigits(candidate))
        {
            workshopId = candidate;
            return true;
        }

        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            error = "Enter a numeric Workshop ID or an HTTP(S) Steam Community URL.";
            return false;
        }

        if (!IsTrustedSteamCommunityHost(uri.IdnHost))
        {
            error = "Workshop URLs must use the steamcommunity.com domain.";
            return false;
        }

        var idValues = ParseQuery(uri.Query)
            .Where(pair => string.Equals(pair.Key, "id", StringComparison.OrdinalIgnoreCase))
            .Select(pair => pair.Value)
            .ToArray();
        if (idValues.Length != 1 || !IsAsciiDigits(idValues[0]))
        {
            error = "The Steam Community URL must contain one numeric id query parameter.";
            return false;
        }

        workshopId = idValues[0];
        return true;
    }

    public static bool IsTrustedSteamCommunityHost(string? host) =>
        !string.IsNullOrWhiteSpace(host)
        && (string.Equals(host, "steamcommunity.com", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".steamcommunity.com", StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<KeyValuePair<string, string>> ParseQuery(string query)
    {
        foreach (var segment in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = segment.IndexOf('=');
            var rawKey = separator < 0 ? segment : segment[..separator];
            var rawValue = separator < 0 ? string.Empty : segment[(separator + 1)..];
            string key;
            string value;
            try
            {
                key = Uri.UnescapeDataString(rawKey.Replace('+', ' '));
                value = Uri.UnescapeDataString(rawValue.Replace('+', ' '));
            }
            catch (UriFormatException)
            {
                continue;
            }

            yield return new KeyValuePair<string, string>(key, value);
        }
    }

    private static bool IsAsciiDigits(string value) =>
        value.Length > 0 && value.All(char.IsAsciiDigit);
}
