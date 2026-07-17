using System.Collections.Concurrent;
using System.Diagnostics;
using StellarisDownloader.Core.Models;
using StellarisDownloader.Core.Services;

namespace StellarisDownloader.Core.Integrations;

public sealed class ProcessRunner : IProcessRunner
{
    public async Task<ProcessRunResult> RunAsync(
        string executablePath,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        TimeSpan timeout,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), "Process timeout must be positive.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start process: {executablePath}");
        var output = new ConcurrentQueue<ProcessOutputLine>();
        long sequence = 0;
        var stdoutTask = ReadOutputAsync(
            process.StandardOutput,
            "stdout",
            output,
            () => Interlocked.Increment(ref sequence),
            progress);
        var stderrTask = ReadOutputAsync(
            process.StandardError,
            "stderr",
            output,
            () => Interlocked.Increment(ref sequence),
            progress);

        using var timeoutSource = new CancellationTokenSource(timeout);
        using var waitSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutSource.Token);

        var timedOut = false;
        var cancelled = false;
        try
        {
            await process.WaitForExitAsync(waitSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            cancelled = cancellationToken.IsCancellationRequested;
            timedOut = !cancelled && timeoutSource.IsCancellationRequested;
            TryKillProcessTree(process);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        }

        await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
        return new ProcessRunResult
        {
            ExitCode = process.HasExited ? process.ExitCode : null,
            TimedOut = timedOut,
            Cancelled = cancelled,
            Output = output.OrderBy(line => line.Sequence).ToArray(),
        };
    }

    private static async Task ReadOutputAsync(
        StreamReader reader,
        string source,
        ConcurrentQueue<ProcessOutputLine> output,
        Func<long> nextSequence,
        IProgress<OperationProgress>? progress)
    {
        while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            var outputLine = new ProcessOutputLine(nextSequence(), source, line);
            output.Enqueue(outputLine);
            progress?.Report(new OperationProgress(
                "ProcessOutput",
                Completed: 0,
                Total: 0,
                WorkshopId: null,
                Message: line));
        }
    }

    private static void TryKillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // The process exited between the state check and the kill request.
        }
    }
}
