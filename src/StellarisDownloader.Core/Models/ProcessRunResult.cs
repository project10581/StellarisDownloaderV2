namespace StellarisDownloader.Core.Models;

public sealed record ProcessRunResult
{
    public int? ExitCode { get; init; }

    public bool TimedOut { get; init; }

    public bool Cancelled { get; init; }

    public IReadOnlyList<ProcessOutputLine> Output { get; init; } = [];
}
