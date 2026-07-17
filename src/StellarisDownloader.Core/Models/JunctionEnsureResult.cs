namespace StellarisDownloader.Core.Models;

public sealed record JunctionEnsureResult(
    OperationStatus Status,
    string JunctionPath,
    string TargetPath,
    bool Changed,
    string? Error);
