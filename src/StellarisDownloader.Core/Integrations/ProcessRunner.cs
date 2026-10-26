using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using StellarisDownloader.Core.Models;
using StellarisDownloader.Core.Services;

namespace StellarisDownloader.Core.Integrations;

public sealed class ProcessRunner : IProcessRunner
{
    private readonly TimeProvider timeProvider;

    public ProcessRunner(TimeProvider? timeProvider = null)
    {
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<ProcessRunResult> RunAsync(
        string executablePath,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        TimeSpan timeout,
        IProgress<OperationProgress>? progress = null,
        Action<ProcessOutputLine>? outputReceived = null,
        bool idleTimeout = false,
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
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start process: {executablePath}");
        using var timeoutSource = new CancellationTokenSource(timeout, timeProvider);
        using var waitSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutSource.Token);
        var output = new ConcurrentQueue<ProcessOutputLine>();
        var outputGate = new object();
        long sequence = 0;
        void OnOutput(string source, string line)
        {
            lock (outputGate)
            {
                if (idleTimeout)
                {
                    timeoutSource.CancelAfter(timeout);
                }

                var outputLine = new ProcessOutputLine(++sequence, source, line);
                output.Enqueue(outputLine);
                try
                {
                    outputReceived?.Invoke(outputLine);
                    progress?.Report(new OperationProgress(
                        "ProcessOutput", 0, 0, null, line));
                }
                catch
                {
                    waitSource.Cancel();
                    throw;
                }
            }
        }

        var stdoutTask = ReadOutputAsync(
            process.StandardOutput,
            "stdout",
            OnOutput);
        var stderrTask = ReadOutputAsync(
            process.StandardError,
            "stderr",
            OnOutput);

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
        Action<string, string> outputReceived)
    {
        while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            outputReceived(source, line);
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
            // Process.Kill throws if the process exits between HasExited and the kill request.
        }
    }
}
