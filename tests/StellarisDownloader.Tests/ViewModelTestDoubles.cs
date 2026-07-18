using System.Windows.Media;
using StellarisDownloader.App.Services;
using StellarisDownloader.Core.Models;
using StellarisDownloader.Core.Services;

namespace StellarisDownloader.Tests;

internal sealed class StubLibraryService : ILibraryService
{
    public LibraryScanResult? ScanResult { get; set; }

    public TaskCompletionSource<LibraryScanResult>? PendingScan { get; set; }

    public Task<LibraryValidationResult> ValidateAsync(
        string? libraryRoot,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new LibraryValidationResult(
            !string.IsNullOrWhiteSpace(libraryRoot),
            libraryRoot,
            string.IsNullOrWhiteSpace(libraryRoot) ? "Missing root." : null));

    public Task<JunctionEnsureResult> EnsureJunctionAsync(
        string libraryRoot,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new JunctionEnsureResult(
            OperationStatus.Succeeded,
            "junction",
            libraryRoot,
            Changed: false,
            Error: null));

    public Task<LibraryScanResult> ScanAsync(
        string libraryRoot,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (PendingScan is not null)
        {
            return PendingScan.Task;
        }

        return Task.FromResult(ScanResult ?? new LibraryScanResult
        {
            Status = OperationStatus.Succeeded,
            LibraryRoot = libraryRoot,
        });
    }

    public Task<LibrarySwitchResult> SwitchAsync(
        AppSettings proposedSettings,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new LibrarySwitchResult
        {
            Status = OperationStatus.Succeeded,
            RequestedLibraryRoot = proposedSettings.LibraryRoot ?? string.Empty,
            SettingsCommitted = true,
        });
}

internal sealed class StubPreviewImageService : IPreviewImageService
{
    private readonly Queue<TaskCompletionSource<ImageSource?>> responses = [];

    public int CallCount { get; private set; }

    public TaskCompletionSource<ImageSource?> EnqueueResponse()
    {
        var response = new TaskCompletionSource<ImageSource?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        responses.Enqueue(response);
        return response;
    }

    public Task<ImageSource?> LoadAsync(
        string? previewUrl,
        CancellationToken cancellationToken = default)
    {
        CallCount++;
        return responses.Count == 0
            ? Task.FromResult<ImageSource?>(null)
            : responses.Dequeue().Task;
    }
}
