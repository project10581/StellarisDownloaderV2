using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using StellarisDownloader.Core.Models;
using StellarisDownloader.Core.Services;

namespace StellarisDownloader.Core.Integrations;

public sealed class WorkshopClient : IWorkshopClient
{
    public const int MaximumBatchSize = 100;
    public const int MaximumConcurrentBatches = 2;

    private static readonly Uri DefaultEndpoint = new(
        "https://api.steampowered.com/ISteamRemoteStorage/GetPublishedFileDetails/v1/");

    private readonly HttpClient httpClient;
    private readonly Uri endpoint;

    public WorkshopClient(HttpClient httpClient, Uri? endpoint = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        this.httpClient = httpClient;
        this.endpoint = endpoint ?? DefaultEndpoint;
    }

    public async Task<IReadOnlyDictionary<string, WorkshopMetadata>> GetMetadataBatchAsync(
        IReadOnlyCollection<string> workshopIds,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workshopIds);
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
        return batchResults
            .SelectMany(result => result)
            .ToDictionary(metadata => metadata.WorkshopId, StringComparer.Ordinal);
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
}
