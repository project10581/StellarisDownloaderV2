using System.Windows.Media;
using StellarisDownloader.App.Services;
using StellarisDownloader.Core.Models;
using StellarisDownloader.Core.Services;

namespace StellarisDownloader.Tests;

internal sealed class StubLibraryService : ILibraryService
{
    public LibraryValidationResult? ValidationResult { get; set; }

    public LibraryScanResult? ScanResult { get; set; }

    public TaskCompletionSource<LibraryScanResult>? PendingScan { get; set; }

    public LibrarySwitchResult? SwitchResult { get; set; }

    public JunctionEnsureResult? JunctionResult { get; set; }

    public int EnsureJunctionCount { get; private set; }

    public int ScanCount { get; private set; }

    public int SwitchCount { get; private set; }

    public AppSettings? LastProposedSettings { get; private set; }

    public Task<LibraryValidationResult> ValidateAsync(
        string? libraryRoot,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(ValidationResult ?? new LibraryValidationResult(
            !string.IsNullOrWhiteSpace(libraryRoot),
            libraryRoot,
            string.IsNullOrWhiteSpace(libraryRoot) ? "Missing root." : null));
    }

    public Task<JunctionEnsureResult> EnsureJunctionAsync(
        string libraryRoot,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureJunctionCount++;
        return Task.FromResult(JunctionResult ?? new JunctionEnsureResult(
            OperationStatus.Succeeded,
            "junction",
            libraryRoot,
            Changed: false,
            Error: null));
    }

    public Task<LibraryScanResult> ScanAsync(
        string libraryRoot,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ScanCount++;
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
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SwitchCount++;
        LastProposedSettings = proposedSettings;
        return Task.FromResult(SwitchResult ?? new LibrarySwitchResult
        {
            Status = OperationStatus.Succeeded,
            RequestedLibraryRoot = proposedSettings.LibraryRoot ?? string.Empty,
            SettingsCommitted = true,
        });
    }
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

internal sealed class StubLocalizationService : ILocalizationService
{
    public string CurrentLanguage { get; private set; } = AppSettings.DefaultLanguage;

    public int SetLanguageCount { get; private set; }

    public void SetLanguage(string language)
    {
        if (language is not AppSettings.DefaultLanguage and not AppSettings.SimplifiedChineseLanguage)
        {
            throw new ArgumentOutOfRangeException(nameof(language));
        }

        SetLanguageCount++;
        CurrentLanguage = language;
    }
}
