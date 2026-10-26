using StellarisDownloader.Core.Models;

namespace StellarisDownloader.Core.Services;

public interface IProcessRunner
{
    Task<ProcessRunResult> RunAsync(
        string executablePath,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        TimeSpan timeout,
        IProgress<OperationProgress>? progress = null,
        Action<ProcessOutputLine>? outputReceived = null,
        bool idleTimeout = false,
        CancellationToken cancellationToken = default);
}
