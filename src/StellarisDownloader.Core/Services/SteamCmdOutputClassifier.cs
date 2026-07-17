using System.Text.RegularExpressions;
using StellarisDownloader.Core.Models;

namespace StellarisDownloader.Core.Services;

public static class SteamCmdOutputClassifier
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);

    public static SteamCmdTerminalState Classify(
        string workshopId,
        IReadOnlyList<ProcessOutputLine> output)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workshopId);
        ArgumentNullException.ThrowIfNull(output);
        if (!workshopId.All(char.IsAsciiDigit))
        {
            throw new ArgumentException("Workshop ID must contain only ASCII digits.", nameof(workshopId));
        }

        var escapedId = Regex.Escape(workshopId);
        var successPattern = new Regex(
            $@"\bSuccess\.\s+Downloaded item\s+{escapedId}\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            RegexTimeout);
        var failurePattern = new Regex(
            $@"\b(?:ERROR!\s*)?Download item\s+{escapedId}\s+failed\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            RegexTimeout);

        var state = SteamCmdTerminalState.Unknown;
        foreach (var line in output.OrderBy(line => line.Sequence))
        {
            if (successPattern.IsMatch(line.Text))
            {
                state = SteamCmdTerminalState.Succeeded;
            }

            if (failurePattern.IsMatch(line.Text))
            {
                state = SteamCmdTerminalState.Failed;
            }
        }

        return state;
    }
}
