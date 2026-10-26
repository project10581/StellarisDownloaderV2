using System.Diagnostics;
using StellarisDownloader.Core.Integrations;

namespace StellarisDownloader.Tests;

public sealed class ProcessRunnerTests
{
    [Fact]
    public async Task IdleTimeoutResetsOnEveryOutputLineUsingInjectedClock()
    {
        using var clock = new ManualTimeProvider();
        var runner = new ProcessRunner(clock);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = runner.RunAsync(
            "powershell.exe",
            ["-NoLogo", "-NoProfile", "-NonInteractive", "-Command", "[Console]::Out.WriteLine('one'); [Console]::Error.WriteLine('two'); [Console]::Out.WriteLine('ready'); Wait-Event"],
            Environment.CurrentDirectory,
            TimeSpan.FromMinutes(10),
            outputReceived: line =>
            {
                if (line.Sequence < 3) { clock.Advance(TimeSpan.FromMinutes(9)); }
                else { ready.TrySetResult(); }
            },
            idleTimeout: true);

        await ready.Task.WaitAsync(TimeSpan.FromSeconds(15));
        clock.Advance(TimeSpan.FromMinutes(9));
        Assert.False(run.IsCompleted);
        clock.Advance(TimeSpan.FromMinutes(1));
        var result = await run.WaitAsync(TimeSpan.FromSeconds(15));

        Assert.True(result.TimedOut);
        Assert.False(result.Cancelled);
        Assert.Equal(3, result.Output.Count);
    }

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
                "[Console]::OutputEncoding = [System.Text.Encoding]::UTF8; [Console]::Out.WriteLine('stdout-line 正在验证安装'); [Console]::Error.WriteLine('stderr-line'); exit 7",
            ],
            Environment.CurrentDirectory,
            TimeSpan.FromSeconds(10));

        Assert.Equal(7, result.ExitCode);
        Assert.False(result.TimedOut);
        Assert.False(result.Cancelled);
        Assert.Contains(result.Output, line => line.Source == "stdout" && line.Text == "stdout-line 正在验证安装");
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
