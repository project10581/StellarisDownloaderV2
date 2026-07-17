namespace StellarisDownloader.Core.Models;

public sealed record LibraryValidationResult(
    bool IsValid,
    string? NormalizedPath,
    string? Error);
