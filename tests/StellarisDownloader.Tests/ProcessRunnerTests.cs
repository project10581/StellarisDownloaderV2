using System.Diagnostics;
using StellarisDownloader.Core.Integrations;

namespace StellarisDownloader.Tests;

public sealed class ProcessRunnerTests
{
    [Fact]
    public async Task CapturesStdoutStderrAndDiagnosticExitCode()
    {
        var runner = new ProcessRunner();

        var result = await runner.RunAsync(
            "powershell.exe",
            [
                "-NoLogo",
                "-NoProfile",
                "-NonInteractive",
                "-Command",
                "[Console]::Out.WriteLine('stdout-line'); [Console]::Error.WriteLine('stderr-line'); exit 7",
            ],
            Environment.CurrentDirectory,
            TimeSpan.FromSeconds(10));

        Assert.Equal(7, result.ExitCode);
        Assert.False(result.TimedOut);
        Assert.False(result.Cancelled);
        Assert.Contains(result.Output, line => line.Source == "stdout" && line.Text == "stdout-line");
        Assert.Contains(result.Output, line => line.Source == "stderr" && line.Text == "stderr-line");
    }

    [Fact]
    public async Task TimeoutKillsTheProcessTreeAndReturnsPromptly()
    {
        var runner = new ProcessRunner();
        var stopwatch = Stopwatch.StartNew();

        var result = await runner.RunAsync(
            "powershell.exe",
            ["-NoLogo", "-NoProfile", "-NonInteractive", "-Command", "Start-Sleep -Seconds 30"],
            Environment.CurrentDirectory,
            TimeSpan.FromMilliseconds(200));

        stopwatch.Stop();
        Assert.True(result.TimedOut);
        Assert.False(result.Cancelled);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task UserCancellationKillsTheProcessTreeAndIsDistinguishedFromTimeout()
    {
        var runner = new ProcessRunner();
        using var cancellationSource = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        var result = await runner.RunAsync(
            "powershell.exe",
            ["-NoLogo", "-NoProfile", "-NonInteractive", "-Command", "Start-Sleep -Seconds 30"],
            Environment.CurrentDirectory,
            TimeSpan.FromSeconds(30),
            cancellationToken: cancellationSource.Token);

        Assert.True(result.Cancelled);
        Assert.False(result.TimedOut);
    }
}
