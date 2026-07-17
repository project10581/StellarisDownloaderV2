namespace StellarisDownloader.Core.Models;

public sealed record JunctionState(
    JunctionStateKind Kind,
    string Path,
    string? TargetPath);
