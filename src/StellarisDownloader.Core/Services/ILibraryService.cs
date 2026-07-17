using StellarisDownloader.Core.Models;

namespace StellarisDownloader.Core.Services;

public interface ILibraryService
{
    Task<LibraryValidationResult> ValidateAsync(
        string? libraryRoot,
        CancellationToken cancellationToken = default);

    Task<JunctionEnsureResult> EnsureJunctionAsync(
        string libraryRoot,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task<LibraryScanResult> ScanAsync(
        string libraryRoot,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task<LibrarySwitchResult> SwitchAsync(
        AppSettings proposedSettings,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
