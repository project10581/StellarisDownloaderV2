namespace StellarisDownloader.Core.Models;

public sealed record LibrarySwitchResult
{
    public required OperationStatus Status { get; init; }

    public required string RequestedLibraryRoot { get; init; }

    public string? PreviousLibraryRoot { get; init; }

    public bool SettingsCommitted { get; init; }

    public bool JunctionChanged { get; init; }

    public bool CanRetryScan { get; init; }

    public bool RequiresManualRepair { get; init; }

    public LibraryScanResult? ScanResult { get; init; }

    public string? Error { get; init; }
}
