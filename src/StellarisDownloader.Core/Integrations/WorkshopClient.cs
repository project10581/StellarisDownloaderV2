using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using StellarisDownloader.Core.Models;
using StellarisDownloader.Core.Services;

namespace StellarisDownloader.Core.Integrations;

public sealed class WorkshopClient : IWorkshopClient, IDisposable
{
    public const int MaximumBatchSize = 100;
    public const int MaximumConcurrentBatches = 2;
    public const int MaximumPersonaBatchSize = 200;
    public static readonly TimeSpan PersonaLookupCooldown = TimeSpan.FromMinutes(5);

    private static readonly Uri DefaultEndpoint = new(
        "https://api.steampowered.com/ISteamRemoteStorage/GetPublishedFileDetails/v1/");
    private static readonly Uri PersonaEndpoint = new(
        "https://steamcommunity.com/actions/ajaxresolveusers");

    private const int MaximumPersonaResponseBytes = 1024 * 1024;

    private readonly HttpClient httpClient;
    private readonly Uri endpoint;
    private readonly TimeProvider timeProvider;
    private readonly ConcurrentDictionary<string, string> creatorNameCache = new(
        StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> missingCreatorCache = new(
        StringComparer.Ordinal);
    private readonly SemaphoreSlim personaLookupGate = new(1, 1);
    private DateTimeOffset? personaLookupRetryAfterUtc;
    private bool disposed;

    public WorkshopClient(
        HttpClient httpClient,
        Uri? endpoint = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        this.httpClient = httpClient;
        this.endpoint = endpoint ?? DefaultEndpoint;
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<IReadOnlyDictionary<string, WorkshopMetadata>> GetMetadataBatchAsync(
        IReadOnlyCollection<string> workshopIds,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workshopIds);
        ObjectDisposedException.ThrowIf(disposed, this);
        var normalizedIds = workshopIds
            .Where(IsValidWorkshopId)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (normalizedIds.Length == 0)
        {
            return new Dictionary<string, WorkshopMetadata>(StringComparer.Ordinal);
        }

        var batches = normalizedIds.Chunk(MaximumBatchSize).ToArray();
        using var concurrencyGate = new SemaphoreSlim(MaximumConcurrentBatches);
        var completed = 0;
        var tasks = batches.Select(async batch =>
        {
            await concurrencyGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await FetchBatchAsync(batch, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                var current = Interlocked.Increment(ref completed);
                progress?.Report(new OperationProgress(
                    "FetchingWorkshopMetadata",
                    current,
                    batches.Length,
                    WorkshopId: null,
                    Message: $"Fetched Workshop metadata batch {current} of {batches.Length}."));
                concurrencyGate.Release();
            }
        }).ToArray();

        var batchResults = await Task.WhenAll(tasks).ConfigureAwait(false);
        var metadata = batchResults.SelectMany(result => result).ToArray();
        metadata = await EnrichCreatorNamesAsync(
            metadata,
            progress,
            cancellationToken).ConfigureAwait(false);
        return metadata.ToDictionary(item => item.WorkshopId, StringComparer.Ordinal);
    }

    private async Task<WorkshopMetadata[]> EnrichCreatorNamesAsync(
        WorkshopMetadata[] metadata,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        var unresolvedCreatorIds = GetUnresolvedCreatorIds(metadata);
        if (unresolvedCreatorIds.Length > 0 && IsPersonaLookupAvailable())
        {
            await personaLookupGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                unresolvedCreatorIds = GetUnresolvedCreatorIds(metadata);
                if (unresolvedCreatorIds.Length > 0 && IsPersonaLookupAvailable())
                {
                    var batches = unresolvedCreatorIds.Chunk(MaximumPersonaBatchSize).ToArray();
                    for (var index = 0; index < batches.Length && IsPersonaLookupAvailable(); index++)
                    {
                        await FetchCreatorNamesAsync(
                            batches[index],
                            cancellationToken).ConfigureAwait(false);
                        if (IsPersonaLookupAvailable())
                        {
                            progress?.Report(new OperationProgress(
                                "FetchingWorkshopAuthors",
                                Completed: index + 1,
                                Total: batches.Length,
                                WorkshopId: null,
                                Message: "Resolved Workshop author display names."));
                        }
                    }
                }
            }
            finally
            {
                personaLookupGate.Release();
            }
        }

        return metadata
            .Select(item => item.CreatorId is not null
                && creatorNameCache.TryGetValue(item.CreatorId, out var creatorName)
                    ? item with { CreatorName = creatorName }
                    : item)
            .ToArray();
    }

    private string[] GetUnresolvedCreatorIds(WorkshopMetadata[] metadata) =>
        metadata
            .Select(item => item.CreatorId)
            .Where(IsValidCreatorId)
            .Select(creatorId => creatorId!)
            .Distinct(StringComparer.Ordinal)
            .Where(creatorId => !creatorNameCache.ContainsKey(creatorId))
            .Where(creatorId => !missingCreatorCache.ContainsKey(creatorId))
            .ToArray();

    private bool IsPersonaLookupAvailable() =>
        personaLookupRetryAfterUtc is null
        || timeProvider.GetUtcNow() >= personaLookupRetryAfterUtc.Value;

    private void TripPersonaLookupCircuit() =>
        personaLookupRetryAfterUtc = timeProvider.GetUtcNow().Add(PersonaLookupCooldown);

    private async Task FetchCreatorNamesAsync(
        IReadOnlyCollection<string> creatorIds,
        CancellationToken cancellationToken)
    {
        var builder = new UriBuilder(PersonaEndpoint)
        {
            Query = $"steamids={Uri.EscapeDataString(string.Join(',', creatorIds))}",
        };
        using var request = new HttpRequestMessage(HttpMethod.Get, builder.Uri);
        request.Headers.UserAgent.ParseAdd("StellarisDownloaderV2/0.1");
        request.Headers.Accept.ParseAdd("application/json");

        try
        {
            using var response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode
                || response.Content.Headers.ContentLength > MaximumPersonaResponseBytes)
            {
                TripPersonaLookupCircuit();
                return;
            }

            var responseBytes = await ReadPersonaResponseAsync(
                response.Content,
                cancellationToken).ConfigureAwait(false);
            if (responseBytes is null)
            {
                TripPersonaLookupCircuit();
                return;
            }

            using var document = JsonDocument.Parse(
                responseBytes,
                new JsonDocumentOptions { MaxDepth = 16 });
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                TripPersonaLookupCircuit();
                return;
            }

            var requestedIds = creatorIds.ToHashSet(StringComparer.Ordinal);
            var resolvedIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in document.RootElement.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var creatorId = GetString(entry, "steamid");
                var creatorName = GetString(entry, "persona_name")?.Trim();
                if (creatorId is null
                    || !requestedIds.Contains(creatorId)
                    || !IsValidCreatorName(creatorName))
                {
                    continue;
                }

                creatorNameCache[creatorId] = creatorName!;
                resolvedIds.Add(creatorId);
            }

            foreach (var creatorId in requestedIds.Except(resolvedIds, StringComparer.Ordinal))
            {
                missingCreatorCache.TryAdd(creatorId, 0);
            }

            personaLookupRetryAfterUtc = null;
        }
        catch (HttpRequestException)
        {
            TripPersonaLookupCircuit();
        }
        catch (JsonException)
        {
            TripPersonaLookupCircuit();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TripPersonaLookupCircuit();
        }
    }

    private static async Task<byte[]?> ReadPersonaResponseAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        await using var source = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var destination = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var bytesRead = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (bytesRead == 0)
            {
                return destination.ToArray();
            }

            if (destination.Length + bytesRead > MaximumPersonaResponseBytes)
            {
                return null;
            }

            await destination.WriteAsync(
                buffer.AsMemory(0, bytesRead),
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<IReadOnlyList<WorkshopMetadata>> FetchBatchAsync(
        IReadOnlyList<string> workshopIds,
        CancellationToken cancellationToken)
    {
        using var content = new FormUrlEncodedContent(BuildFormValues(workshopIds));
        try
        {
            using var response = await httpClient.PostAsync(
                endpoint,
                content,
                cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return [];
            }

            using var document = await response.Content.ReadFromJsonAsync<JsonDocument>(
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (document is null
                || !document.RootElement.TryGetProperty("response", out var responseElement)
                || !responseElement.TryGetProperty("publishedfiledetails", out var detailsElement)
                || detailsElement.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var metadata = new List<WorkshopMetadata>();
            foreach (var detail in detailsElement.EnumerateArray())
            {
                var parsed = ParseMetadata(detail);
                if (parsed is not null)
                {
                    metadata.Add(parsed);
                }
            }

            return metadata;
        }
        catch (HttpRequestException)
        {
            return [];
        }
        catch (JsonException)
        {
            return [];
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return [];
        }
    }

    private static IEnumerable<KeyValuePair<string, string>> BuildFormValues(
        IReadOnlyList<string> workshopIds)
    {
        yield return new KeyValuePair<string, string>(
            "itemcount",
            workshopIds.Count.ToString(CultureInfo.InvariantCulture));
        for (var index = 0; index < workshopIds.Count; index++)
        {
            yield return new KeyValuePair<string, string>(
                $"publishedfileids[{index}]",
                workshopIds[index]);
        }
    }

    private static WorkshopMetadata? ParseMetadata(JsonElement detail)
    {
        if (GetInt32(detail, "result") != 1)
        {
            return null;
        }

        var workshopId = GetString(detail, "publishedfileid");
        if (workshopId is null || !IsValidWorkshopId(workshopId))
        {
            return null;
        }

        return new WorkshopMetadata
        {
            WorkshopId = workshopId,
            AppId = GetInt32(detail, "consumer_app_id") ?? 281990,
            Title = GetString(detail, "title"),
            Description = GetString(detail, "description"),
            PreviewUrl = GetString(detail, "preview_url"),
            CreatorId = GetString(detail, "creator"),
            CreatedAtUtc = GetUnixTimestamp(detail, "time_created"),
            LatestRemoteUpdatedAtUtc = GetUnixTimestamp(detail, "time_updated"),
            FileSize = GetInt64(detail, "file_size"),
        };
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.String => property.GetString(),
            JsonValueKind.Number => property.GetRawText(),
            _ => null,
        };
    }

    private static int? GetInt32(JsonElement element, string propertyName)
    {
        var value = GetString(element, propertyName);
        return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    private static long? GetInt64(JsonElement element, string propertyName)
    {
        var value = GetString(element, propertyName);
        return long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    private static DateTimeOffset? GetUnixTimestamp(JsonElement element, string propertyName)
    {
        var seconds = GetInt64(element, propertyName);
        return seconds is null ? null : DateTimeOffset.FromUnixTimeSeconds(seconds.Value);
    }

    private static bool IsValidWorkshopId(string? workshopId) =>
        !string.IsNullOrWhiteSpace(workshopId) && workshopId.All(char.IsAsciiDigit);

    private static bool IsValidCreatorId(string? creatorId) =>
        !string.IsNullOrWhiteSpace(creatorId) && creatorId.All(char.IsAsciiDigit);

    private static bool IsValidCreatorName(string? creatorName) =>
        !string.IsNullOrWhiteSpace(creatorName)
        && creatorName.Length <= 128
        && !creatorName.Any(char.IsControl);

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        personaLookupGate.Dispose();
        disposed = true;
        GC.SuppressFinalize(this);
    }
}
