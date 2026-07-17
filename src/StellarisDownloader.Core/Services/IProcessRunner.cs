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
        CancellationToken cancellationToken = default);
}
