using StellarisDownloader.Core.Models;
using StellarisDownloader.Core.Services;

namespace StellarisDownloader.Tests;

public sealed class SteamCmdOutputClassifierTests
{
    [Fact]
    public async Task HistoricalFailureFollowedBySuccessUsesTheFinalTerminalState()
    {
        var output = await LoadFixtureAsync("historical-failure-then-success.log");

        var result = SteamCmdOutputClassifier.Classify("123456789", output);

        Assert.Equal(SteamCmdTerminalState.Succeeded, result);
    }

    [Fact]
    public async Task FailureAfterSuccessUsesTheFinalTerminalState()
    {
        var output = await LoadFixtureAsync("success-then-final-failure.log");

        var result = SteamCmdOutputClassifier.Classify("123456789", output);

        Assert.Equal(SteamCmdTerminalState.Failed, result);
    }

    [Fact]
    public async Task OutputWithoutATerminalResultIsUnknown()
    {
        var output = await LoadFixtureAsync("unclassified.log");

        var result = SteamCmdOutputClassifier.Classify("123456789", output);

        Assert.Equal(SteamCmdTerminalState.Unknown, result);
    }

    [Fact]
    public void TerminalResultForAnotherIdIsIgnored()
    {
        ProcessOutputLine[] output =
        [
            new(1, "stdout", "Success. Downloaded item 987654321 to a folder."),
            new(2, "stderr", "ERROR! Download item 987654321 failed (Failure)."),
        ];

        var result = SteamCmdOutputClassifier.Classify("123456789", output);

        Assert.Equal(SteamCmdTerminalState.Unknown, result);
    }

    private static async Task<IReadOnlyList<ProcessOutputLine>> LoadFixtureAsync(string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "SteamCmd", fileName);
        var lines = await File.ReadAllLinesAsync(path);
        return lines
            .Select((line, index) => new ProcessOutputLine(index + 1, "stdout", line))
            .ToArray();
    }
}
