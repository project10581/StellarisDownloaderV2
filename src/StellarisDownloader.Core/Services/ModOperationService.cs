using StellarisDownloader.Core.Models;

namespace StellarisDownloader.Core.Services;

public sealed class ModOperationService : IModOperationService
{
    private readonly IModRepository modRepository;
    private readonly ISteamCmdService steamCmdService;
    private readonly IWorkshopClient workshopClient;
    private readonly IJunctionManager junctionManager;
    private readonly WriteOperationCoordinator writeCoordinator;
    private readonly string junctionPath;
    private readonly TimeProvider timeProvider;

    public ModOperationService(
        IModRepository modRepository,
        ISteamCmdService steamCmdService,
        IWorkshopClient workshopClient,
        IJunctionManager junctionManager,
        WriteOperationCoordinator writeCoordinator,
        string junctionPath,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(modRepository);
        ArgumentNullException.ThrowIfNull(steamCmdService);
        ArgumentNullException.ThrowIfNull(workshopClient);
        ArgumentNullException.ThrowIfNull(junctionManager);
        ArgumentNullException.ThrowIfNull(writeCoordinator);
        ArgumentException.ThrowIfNullOrWhiteSpace(junctionPath);

        this.modRepository = modRepository;
        this.steamCmdService = steamCmdService;
        this.workshopClient = workshopClient;
        this.junctionManager = junctionManager;
        this.writeCoordinator = writeCoordinator;
        this.junctionPath = NormalizePath(junctionPath);
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<DownloadBatchResult> DownloadBatchAsync(
        IReadOnlyCollection<DownloadRequest> requests,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requests);
        try
        {
            return await writeCoordinator.ExecuteAsync(
                token => DownloadBatchCoreAsync(requests, progress, token),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new DownloadBatchResult
            {
                Results = requests.Select(CancelledBeforeStart).ToArray(),
            };
        }
    }

    public async Task<IReadOnlyList<UpdateCheckResult>> CheckUpdatesAsync(
        string libraryRoot,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var normalizedRoot = NormalizePath(libraryRoot);
        var cacheState = await modRepository.GetCacheStateAsync(
            normalizedRoot,
            cancellationToken).ConfigureAwait(false);
        if (cacheState.State != CacheState.Valid)
        {
            return [];
        }

        var records = await modRepository.ListAsync(normalizedRoot, cancellationToken).ConfigureAwait(false);
        var metadata = await workshopClient.GetMetadataBatchAsync(
            records.Select(record => record.WorkshopId).ToArray(),
            progress,
            cancellationToken).ConfigureAwait(false);
        var results = new List<UpdateCheckResult>(records.Count);

        for (var index = 0; index < records.Count; index++)
        {
            var record = records[index];
            metadata.TryGetValue(record.WorkshopId, out var latest);
            results.Add(BuildUpdateCheckResult(record, latest));
            progress?.Report(new OperationProgress(
                "CheckingModUpdates",
                index + 1,
                records.Count,
                record.WorkshopId,
                $"Checked Workshop item {record.WorkshopId} for updates."));
        }

        return results;
    }

    public async Task<DownloadBatchResult> UpdateSelectedAsync(
        string libraryRoot,
        IReadOnlyCollection<string> workshopIds,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workshopIds);
        var normalizedRoot = NormalizePath(libraryRoot);
        var requests = workshopIds
            .Where(IsValidWorkshopId)
            .Distinct(StringComparer.Ordinal)
            .Select(workshopId => new DownloadRequest
            {
                WorkshopId = workshopId,
                LibraryRoot = normalizedRoot,
            })
            .ToArray();
        return await DownloadBatchAsync(requests, progress, cancellationToken).ConfigureAwait(false);
    }

    private async Task<DownloadBatchResult> DownloadBatchCoreAsync(
        IReadOnlyCollection<DownloadRequest> requests,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        var normalizedRequests = NormalizeAndDeduplicateRequests(requests);
        if (normalizedRequests.Count == 0)
        {
            return new DownloadBatchResult();
        }

        var libraryRoots = normalizedRequests
            .Select(request => request.LibraryRoot)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (libraryRoots.Length != 1)
        {
            return FailEntireBatch(normalizedRequests, "All queue items must use the same library root.");
        }

        var libraryRoot = libraryRoots[0];
        var cacheState = await modRepository.GetCacheStateAsync(
            libraryRoot,
            cancellationToken).ConfigureAwait(false);
        if (cacheState.State != CacheState.Valid)
        {
            return FailEntireBatch(
                normalizedRequests,
                "The mod cache is stale or belongs to a different library root.");
        }

        Directory.CreateDirectory(libraryRoot);
        try
        {
            await junctionManager.SetTargetAsync(
                junctionPath,
                libraryRoot,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsOperationalFailure(exception))
        {
            return FailEntireBatch(normalizedRequests, exception.Message);
        }

        var results = new List<DownloadResult>(normalizedRequests.Count);
        for (var index = 0; index < normalizedRequests.Count; index++)
        {
            var request = normalizedRequests[index];
            if (cancellationToken.IsCancellationRequested)
            {
                AddCancelledRemainder(normalizedRequests, index, results);
                break;
            }

            progress?.Report(new OperationProgress(
                "DownloadingQueue",
                index,
                normalizedRequests.Count,
                request.WorkshopId,
                $"Downloading queue item {index + 1} of {normalizedRequests.Count}."));
            var result = await steamCmdService.DownloadAsync(
                request,
                progress,
                cancellationToken).ConfigureAwait(false);
            results.Add(result);
            if (result.Status == OperationStatus.Cancelled)
            {
                AddCancelledRemainder(normalizedRequests, index + 1, results);
                break;
            }
        }

        await PersistFinalResultsAsync(libraryRoot, results, progress).ConfigureAwait(false);
        return new DownloadBatchResult { Results = results };
    }

    private async Task PersistFinalResultsAsync(
        string libraryRoot,
        IReadOnlyList<DownloadResult> results,
        IProgress<OperationProgress>? progress)
    {
        var successfulIds = results
            .Where(result => result.Status == OperationStatus.Succeeded)
            .Select(result => result.WorkshopId)
            .ToArray();
        IReadOnlyDictionary<string, WorkshopMetadata> metadata;
        try
        {
            metadata = await workshopClient.GetMetadataBatchAsync(
                successfulIds,
                progress,
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsOperationalFailure(exception))
        {
            metadata = new Dictionary<string, WorkshopMetadata>(StringComparer.Ordinal);
        }

        var completedAtUtc = timeProvider.GetUtcNow();
        foreach (var result in results)
        {
            var existing = await modRepository.GetAsync(
                libraryRoot,
                result.WorkshopId,
                CancellationToken.None).ConfigureAwait(false);
            if (result.Status == OperationStatus.Succeeded)
            {
                metadata.TryGetValue(result.WorkshopId, out var itemMetadata);
                var record = BuildSuccessfulRecord(result, itemMetadata, existing, completedAtUtc);
                await modRepository.UpsertFinalResultAsync(
                    libraryRoot,
                    record,
                    CancellationToken.None).ConfigureAwait(false);
            }
            else if (existing is not null)
            {
                await modRepository.UpsertFinalResultAsync(
                    libraryRoot,
                    existing with
                    {
                        LastOperationStatus = result.Status,
                        LastError = result.Error,
                        LastScannedAtUtc = completedAtUtc,
                    },
                    CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    private static ModRecord BuildSuccessfulRecord(
        DownloadResult result,
        WorkshopMetadata? metadata,
        ModRecord? existing,
        DateTimeOffset completedAtUtc)
    {
        return new ModRecord
        {
            WorkshopId = result.WorkshopId,
            AppId = metadata?.AppId ?? existing?.AppId ?? SteamCmdService.StellarisAppId,
            Title = metadata?.Title ?? existing?.Title,
            Description = metadata?.Description ?? existing?.Description,
            PreviewUrl = metadata?.PreviewUrl ?? existing?.PreviewUrl,
            CreatorId = metadata?.CreatorId ?? existing?.CreatorId,
            CreatedAtUtc = metadata?.CreatedAtUtc ?? existing?.CreatedAtUtc,
            ContentPath = result.ContentPath,
            FileSize = metadata?.FileSize ?? existing?.FileSize,
            ImportedOrDownloadedAtUtc = completedAtUtc,
            InstalledWorkshopUpdatedAtUtc = metadata?.LatestRemoteUpdatedAtUtc
                ?? existing?.InstalledWorkshopUpdatedAtUtc,
            LocalState = LocalModState.Available,
            LastOperationStatus = OperationStatus.Succeeded,
            LastScannedAtUtc = completedAtUtc,
        };
    }

    private static UpdateCheckResult BuildUpdateCheckResult(
        ModRecord record,
        WorkshopMetadata? metadata)
    {
        if (metadata?.LatestRemoteUpdatedAtUtc is null)
        {
            return new UpdateCheckResult
            {
                WorkshopId = record.WorkshopId,
                Title = metadata?.Title ?? record.Title,
                State = UpdateState.CheckFailed,
                InstalledWorkshopUpdatedAtUtc = record.InstalledWorkshopUpdatedAtUtc,
                Error = "Workshop metadata is unavailable or missing its remote update time.",
            };
        }

        var usesApproximation = record.InstalledWorkshopUpdatedAtUtc is null;
        var installedReference = record.InstalledWorkshopUpdatedAtUtc
            ?? record.ImportedOrDownloadedAtUtc;
        return new UpdateCheckResult
        {
            WorkshopId = record.WorkshopId,
            Title = metadata.Title ?? record.Title,
            State = metadata.LatestRemoteUpdatedAtUtc > installedReference
                ? UpdateState.UpdateAvailable
                : UpdateState.UpToDate,
            LatestRemoteUpdatedAtUtc = metadata.LatestRemoteUpdatedAtUtc,
            InstalledWorkshopUpdatedAtUtc = record.InstalledWorkshopUpdatedAtUtc,
            UsesApproximateLocalTimestamp = usesApproximation,
        };
    }

    private static List<DownloadRequest> NormalizeAndDeduplicateRequests(
        IReadOnlyCollection<DownloadRequest> requests)
    {
        var normalized = new List<DownloadRequest>(requests.Count);
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var request in requests)
        {
            ArgumentNullException.ThrowIfNull(request);
            if (!seenIds.Add(request.WorkshopId))
            {
                continue;
            }

            normalized.Add(request with { LibraryRoot = NormalizePath(request.LibraryRoot) });
        }

        return normalized;
    }

    private static DownloadBatchResult FailEntireBatch(
        IReadOnlyCollection<DownloadRequest> requests,
        string error)
    {
        return new DownloadBatchResult
        {
            Results = requests.Select(request => new DownloadResult
            {
                WorkshopId = request.WorkshopId,
                Status = OperationStatus.Failed,
                ContentPath = BuildContentPath(request),
                Error = error,
            }).ToArray(),
        };
    }

    private static void AddCancelledRemainder(
        List<DownloadRequest> requests,
        int startIndex,
        List<DownloadResult> results)
    {
        for (var index = startIndex; index < requests.Count; index++)
        {
            results.Add(CancelledBeforeStart(requests[index]));
        }
    }

    private static DownloadResult CancelledBeforeStart(DownloadRequest request)
    {
        return new DownloadResult
        {
            WorkshopId = request.WorkshopId,
            Status = OperationStatus.Cancelled,
            ContentPath = BuildContentPath(request),
            Error = "Queue item was cancelled before it started.",
        };
    }

    private static string BuildContentPath(DownloadRequest request) =>
        IsValidWorkshopId(request.WorkshopId)
            ? Path.Combine(NormalizePath(request.LibraryRoot), request.WorkshopId)
            : NormalizePath(request.LibraryRoot);

    private static bool IsOperationalFailure(Exception exception) =>
        exception is HttpRequestException
            or IOException
            or UnauthorizedAccessException
            or InvalidOperationException
            or NotSupportedException;

    private static bool IsValidWorkshopId(string workshopId) =>
        !string.IsNullOrWhiteSpace(workshopId) && workshopId.All(char.IsAsciiDigit);

    private static string NormalizePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }
}
