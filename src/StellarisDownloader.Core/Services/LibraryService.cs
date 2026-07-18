using System.Security;
using Microsoft.Data.Sqlite;
using StellarisDownloader.Core.Models;

namespace StellarisDownloader.Core.Services;

public sealed class LibraryService : ILibraryService
{
    private const string ScanStage = "ScanningLibrary";
    private const string JunctionStage = "EnsuringJunction";

    private readonly ISettingsStore settingsStore;
    private readonly IModRepository modRepository;
    private readonly IWorkshopClient workshopClient;
    private readonly IJunctionManager junctionManager;
    private readonly WriteOperationCoordinator writeCoordinator;
    private readonly string junctionPath;
    private readonly TimeProvider timeProvider;

    public LibraryService(
        ISettingsStore settingsStore,
        IModRepository modRepository,
        IWorkshopClient workshopClient,
        IJunctionManager junctionManager,
        WriteOperationCoordinator writeCoordinator,
        string junctionPath,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(settingsStore);
        ArgumentNullException.ThrowIfNull(modRepository);
        ArgumentNullException.ThrowIfNull(workshopClient);
        ArgumentNullException.ThrowIfNull(junctionManager);
        ArgumentNullException.ThrowIfNull(writeCoordinator);
        ArgumentException.ThrowIfNullOrWhiteSpace(junctionPath);

        this.settingsStore = settingsStore;
        this.modRepository = modRepository;
        this.workshopClient = workshopClient;
        this.junctionManager = junctionManager;
        this.writeCoordinator = writeCoordinator;
        this.junctionPath = NormalizePath(junctionPath);
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public Task<LibraryValidationResult> ValidateAsync(
        string? libraryRoot,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Validate(libraryRoot));
    }

    public Task<JunctionEnsureResult> EnsureJunctionAsync(
        string libraryRoot,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return writeCoordinator.ExecuteAsync(
            token => EnsureJunctionCoreAsync(libraryRoot, progress, token),
            cancellationToken);
    }

    public Task<LibraryScanResult> ScanAsync(
        string libraryRoot,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return writeCoordinator.ExecuteAsync(
            token => ScanWithResultAsync(libraryRoot, progress, token),
            cancellationToken);
    }

    public Task<LibrarySwitchResult> SwitchAsync(
        AppSettings proposedSettings,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(proposedSettings);
        return writeCoordinator.ExecuteAsync(
            token => SwitchCoreAsync(proposedSettings, progress, token),
            cancellationToken);
    }

    private async Task<JunctionEnsureResult> EnsureJunctionCoreAsync(
        string libraryRoot,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        var validation = Validate(libraryRoot);
        var normalizedRoot = validation.NormalizedPath ?? libraryRoot;
        if (!validation.IsValid)
        {
            return new JunctionEnsureResult(
                OperationStatus.Failed,
                junctionPath,
                normalizedRoot,
                Changed: false,
                validation.Error);
        }

        try
        {
            Directory.CreateDirectory(normalizedRoot);
            progress?.Report(new OperationProgress(
                JunctionStage,
                Completed: 0,
                Total: 1,
                WorkshopId: null,
                Message: "Ensuring the SteamCMD workshop junction."));
            var update = await junctionManager.SetTargetAsync(
                junctionPath,
                normalizedRoot,
                cancellationToken).ConfigureAwait(false);
            progress?.Report(new OperationProgress(
                JunctionStage,
                Completed: 1,
                Total: 1,
                WorkshopId: null,
                Message: "SteamCMD workshop junction is ready."));
            return new JunctionEnsureResult(
                OperationStatus.Succeeded,
                junctionPath,
                normalizedRoot,
                update.Changed,
                Error: null);
        }
        catch (Exception exception) when (IsOperationalFailure(exception))
        {
            return new JunctionEnsureResult(
                OperationStatus.Failed,
                junctionPath,
                normalizedRoot,
                Changed: false,
                exception.Message);
        }
    }

    private async Task<LibraryScanResult> ScanWithResultAsync(
        string libraryRoot,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        var validation = Validate(libraryRoot);
        var normalizedRoot = validation.NormalizedPath ?? libraryRoot;
        if (!validation.IsValid)
        {
            return FailedScan(normalizedRoot, validation.Error);
        }

        if (!Directory.Exists(normalizedRoot))
        {
            return FailedScan(normalizedRoot, "The library root does not exist.");
        }

        try
        {
            var previousRecords = await modRepository.ListAsync(
                normalizedRoot,
                cancellationToken).ConfigureAwait(false);
            return await ScanCoreAsync(
                normalizedRoot,
                previousRecords,
                preserveExistingState: true,
                progress,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsOperationalFailure(exception))
        {
            await TryMarkCacheStaleAsync().ConfigureAwait(false);
            return FailedScan(normalizedRoot, exception.Message);
        }
    }

    private async Task<LibrarySwitchResult> SwitchCoreAsync(
        AppSettings proposedSettings,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        var requestedRoot = proposedSettings.LibraryRoot ?? string.Empty;
        var validation = Validate(requestedRoot);
        if (!validation.IsValid || validation.NormalizedPath is null)
        {
            return FailedSwitch(
                requestedRoot,
                previousRoot: null,
                validation.Error ?? "The library root is invalid.");
        }

        var normalizedRoot = validation.NormalizedPath;
        SettingsLoadResult currentSettingsResult;
        IReadOnlyList<ModRecord> previousRecords;
        string? previousRoot;

        try
        {
            currentSettingsResult = await settingsStore.LoadAsync(cancellationToken).ConfigureAwait(false);
            previousRoot = NormalizeOptionalPath(currentSettingsResult.Settings.LibraryRoot);
            previousRecords = previousRoot is null
                ? []
                : await modRepository.ListAsync(previousRoot, cancellationToken).ConfigureAwait(false);
            Directory.CreateDirectory(normalizedRoot);
        }
        catch (Exception exception) when (IsOperationalFailure(exception))
        {
            return FailedSwitch(normalizedRoot, previousRoot: null, exception.Message);
        }

        JunctionUpdate junctionUpdate;
        try
        {
            progress?.Report(new OperationProgress(
                JunctionStage,
                Completed: 0,
                Total: 1,
                WorkshopId: null,
                Message: "Switching the SteamCMD workshop junction."));
            junctionUpdate = await junctionManager.SetTargetAsync(
                junctionPath,
                normalizedRoot,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsOperationalFailure(exception))
        {
            return FailedSwitch(normalizedRoot, previousRoot, exception.Message);
        }

        var normalizedSettings = proposedSettings with { LibraryRoot = normalizedRoot };
        try
        {
            await settingsStore.SaveAsync(normalizedSettings, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception saveException) when (IsOperationalFailure(saveException))
        {
            try
            {
                await junctionManager.RestoreAsync(
                    junctionUpdate,
                    CancellationToken.None).ConfigureAwait(false);
                return FailedSwitch(normalizedRoot, previousRoot, saveException.Message);
            }
            catch (Exception restoreException) when (IsOperationalFailure(restoreException))
            {
                return new LibrarySwitchResult
                {
                    Status = OperationStatus.Failed,
                    RequestedLibraryRoot = normalizedRoot,
                    PreviousLibraryRoot = previousRoot,
                    SettingsCommitted = false,
                    JunctionChanged = junctionUpdate.Changed,
                    RequiresManualRepair = true,
                    Error = $"Settings save failed: {saveException.Message} Junction rollback also failed: {restoreException.Message}",
                };
            }
        }

        try
        {
            await modRepository.MarkCacheStaleAsync(cancellationToken).ConfigureAwait(false);
            var scanResult = await ScanCoreAsync(
                normalizedRoot,
                previousRecords,
                preserveExistingState: previousRoot is not null
                    && string.Equals(previousRoot, normalizedRoot, StringComparison.OrdinalIgnoreCase),
                progress,
                cancellationToken).ConfigureAwait(false);
            progress?.Report(new OperationProgress(
                JunctionStage,
                Completed: 1,
                Total: 1,
                WorkshopId: null,
                Message: "Library switch completed."));

            return new LibrarySwitchResult
            {
                Status = OperationStatus.Succeeded,
                RequestedLibraryRoot = normalizedRoot,
                PreviousLibraryRoot = previousRoot,
                SettingsCommitted = true,
                JunctionChanged = junctionUpdate.Changed,
                ScanResult = scanResult,
            };
        }
        catch (Exception exception) when (IsOperationalFailure(exception))
        {
            await TryMarkCacheStaleAsync().ConfigureAwait(false);
            return new LibrarySwitchResult
            {
                Status = OperationStatus.Failed,
                RequestedLibraryRoot = normalizedRoot,
                PreviousLibraryRoot = previousRoot,
                SettingsCommitted = true,
                JunctionChanged = junctionUpdate.Changed,
                CanRetryScan = true,
                Error = exception.Message,
            };
        }
    }

    private async Task<LibraryScanResult> ScanCoreAsync(
        string normalizedRoot,
        IReadOnlyCollection<ModRecord> previousRecords,
        bool preserveExistingState,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        var directories = new DirectoryInfo(normalizedRoot)
            .EnumerateDirectories()
            .OrderBy(directory => directory.Name, StringComparer.Ordinal)
            .ToArray();
        var workshopDirectories = directories
            .Where(directory => directory.Name.All(char.IsAsciiDigit))
            .ToArray();
        var ignoredDirectoryCount = directories.Length - workshopDirectories.Length;
        var records = new List<ModRecord>(workshopDirectories.Length);
        var emptyWorkshopIds = new List<string>();
        var scannedAtUtc = timeProvider.GetUtcNow();
        var previousRecordsById = previousRecords.ToDictionary(
            record => record.WorkshopId,
            StringComparer.Ordinal);

        progress?.Report(new OperationProgress(
            ScanStage,
            Completed: 0,
            Total: workshopDirectories.Length,
            WorkshopId: null,
            Message: "Scanning the selected mod library."));

        for (var index = 0; index < workshopDirectories.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = workshopDirectories[index];
            var workshopId = directory.Name;
            if (!directory.EnumerateFileSystemInfos().Any())
            {
                emptyWorkshopIds.Add(workshopId);
            }
            else
            {
                previousRecordsById.TryGetValue(workshopId, out var previousRecord);
                records.Add(new ModRecord
                {
                    WorkshopId = workshopId,
                    ContentPath = directory.FullName,
                    ImportedOrDownloadedAtUtc = preserveExistingState && previousRecord is not null
                        ? previousRecord.ImportedOrDownloadedAtUtc
                        : new DateTimeOffset(directory.LastWriteTimeUtc),
                    InstalledWorkshopUpdatedAtUtc = preserveExistingState
                        ? previousRecord?.InstalledWorkshopUpdatedAtUtc
                        : null,
                    LastOperationStatus = preserveExistingState && previousRecord is not null
                        ? previousRecord.LastOperationStatus
                        : OperationStatus.Succeeded,
                    LastError = preserveExistingState ? previousRecord?.LastError : null,
                    LastScannedAtUtc = scannedAtUtc,
                });
            }

            progress?.Report(new OperationProgress(
                ScanStage,
                Completed: index + 1,
                Total: workshopDirectories.Length,
                WorkshopId: workshopId,
                Message: $"Scanned Workshop item {workshopId}."));
        }

        var metadataById = await GetMetadataWithoutBlockingScanAsync(
            records.Select(record => record.WorkshopId).ToArray(),
            progress,
            cancellationToken).ConfigureAwait(false);
        records = records
            .Select(record => MergeMetadata(
                record,
                previousRecordsById.GetValueOrDefault(record.WorkshopId),
                metadataById.GetValueOrDefault(record.WorkshopId)))
            .ToList();

        await modRepository.ReplaceSnapshotAsync(
            normalizedRoot,
            records,
            scannedAtUtc,
            cancellationToken).ConfigureAwait(false);
        records = (await modRepository.ListAsync(
            normalizedRoot,
            CancellationToken.None).ConfigureAwait(false)).ToList();

        var previousIds = previousRecords
            .Select(record => record.WorkshopId)
            .ToHashSet(StringComparer.Ordinal);
        var currentIds = records
            .Select(record => record.WorkshopId)
            .ToHashSet(StringComparer.Ordinal);

        return new LibraryScanResult
        {
            Status = OperationStatus.Succeeded,
            LibraryRoot = normalizedRoot,
            Records = records,
            AddedWorkshopIds = currentIds.Except(previousIds, StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray(),
            RemovedWorkshopIds = previousIds.Except(currentIds, StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray(),
            EmptyWorkshopIds = emptyWorkshopIds,
            IgnoredDirectoryCount = ignoredDirectoryCount,
        };
    }

    private async Task<IReadOnlyDictionary<string, WorkshopMetadata>> GetMetadataWithoutBlockingScanAsync(
        string[] workshopIds,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (workshopIds.Length == 0)
        {
            return new Dictionary<string, WorkshopMetadata>(StringComparer.Ordinal);
        }

        try
        {
            return await workshopClient.GetMetadataBatchAsync(
                workshopIds,
                progress,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsMetadataLookupFailure(exception, cancellationToken))
        {
            return new Dictionary<string, WorkshopMetadata>(StringComparer.Ordinal);
        }
    }

    private static ModRecord MergeMetadata(
        ModRecord scannedRecord,
        ModRecord? previousRecord,
        WorkshopMetadata? metadata)
    {
        return scannedRecord with
        {
            AppId = metadata?.AppId ?? previousRecord?.AppId ?? scannedRecord.AppId,
            Title = metadata?.Title ?? previousRecord?.Title,
            Description = metadata?.Description ?? previousRecord?.Description,
            PreviewUrl = metadata?.PreviewUrl ?? previousRecord?.PreviewUrl,
            CreatorId = metadata?.CreatorId ?? previousRecord?.CreatorId,
            CreatedAtUtc = metadata?.CreatedAtUtc ?? previousRecord?.CreatedAtUtc,
            FileSize = metadata?.FileSize ?? previousRecord?.FileSize,
        };
    }

    private LibraryValidationResult Validate(string? libraryRoot)
    {
        if (string.IsNullOrWhiteSpace(libraryRoot))
        {
            return new LibraryValidationResult(false, NormalizedPath: null, "Library root is not configured.");
        }

        string normalizedRoot;
        try
        {
            normalizedRoot = NormalizePath(libraryRoot);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException or SecurityException)
        {
            return new LibraryValidationResult(false, NormalizedPath: null, exception.Message);
        }

        if (File.Exists(normalizedRoot))
        {
            return new LibraryValidationResult(false, normalizedRoot, "Library root is a file, not a directory.");
        }

        if (PathsOverlap(normalizedRoot, junctionPath))
        {
            return new LibraryValidationResult(
                false,
                normalizedRoot,
                "Library root must not contain or be contained by the SteamCMD junction path.");
        }

        return new LibraryValidationResult(true, normalizedRoot, Error: null);
    }

    private async Task TryMarkCacheStaleAsync()
    {
        try
        {
            await modRepository.MarkCacheStaleAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsOperationalFailure(exception))
        {
            // The root binding still prevents displaying old rows if recording stale state fails.
        }
    }

    private static LibraryScanResult FailedScan(string libraryRoot, string? error)
    {
        return new LibraryScanResult
        {
            Status = OperationStatus.Failed,
            LibraryRoot = libraryRoot,
            Error = error,
        };
    }

    private static LibrarySwitchResult FailedSwitch(
        string requestedRoot,
        string? previousRoot,
        string error)
    {
        return new LibrarySwitchResult
        {
            Status = OperationStatus.Failed,
            RequestedLibraryRoot = requestedRoot,
            PreviousLibraryRoot = previousRoot,
            Error = error,
        };
    }

    private static bool IsOperationalFailure(Exception exception)
    {
        return exception is IOException
            or UnauthorizedAccessException
            or InvalidOperationException
            or InvalidDataException
            or ArgumentException
            or NotSupportedException
            or SecurityException
            or SqliteException;
    }

    private static bool IsMetadataLookupFailure(
        Exception exception,
        CancellationToken cancellationToken) =>
        exception is OperationCanceledException
            ? !cancellationToken.IsCancellationRequested
            : exception is HttpRequestException
                or IOException
                or InvalidDataException
                or TimeoutException
                or System.Text.Json.JsonException;

    private static string? NormalizeOptionalPath(string? path) =>
        string.IsNullOrWhiteSpace(path) ? null : NormalizePath(path);

    private static string NormalizePath(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static bool PathsOverlap(string left, string right) =>
        IsSameOrAncestor(left, right) || IsSameOrAncestor(right, left);

    private static bool IsSameOrAncestor(string ancestor, string descendant)
    {
        if (string.Equals(ancestor, descendant, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var ancestorWithSeparator = Path.EndsInDirectorySeparator(ancestor)
            ? ancestor
            : ancestor + Path.DirectorySeparatorChar;
        return descendant.StartsWith(ancestorWithSeparator, StringComparison.OrdinalIgnoreCase);
    }
}
