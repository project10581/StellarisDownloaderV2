using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using StellarisDownloader.Core.Integrations;

namespace StellarisDownloader.Tests;

public sealed class WorkshopClientTests
{
    [Fact]
    public async Task BatchRequestDeduplicatesIdsAndParsesSteamResponse()
    {
        string? requestBody = null;
        using var handler = new StubHttpMessageHandler(async (request, cancellationToken) =>
        {
            requestBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            return StubHttpMessageHandler.Response(
                HttpStatusCode.OK,
                JsonContent.Create(new
                {
                    response = new
                    {
                        publishedfiledetails = new object[]
                        {
                            new
                            {
                                publishedfileid = "123",
                                result = 1,
                                consumer_app_id = 281990,
                                title = "A Mod",
                                description = "Description",
                                preview_url = "https://example.invalid/preview.png",
                                creator = "42",
                                time_created = 1_700_000_000,
                                time_updated = 1_800_000_000,
                                file_size = "4096",
                            },
                            new { publishedfileid = "456", result = 9 },
                        },
                    },
                }));
        });
        using var httpClient = new HttpClient(handler);
        var client = new WorkshopClient(httpClient);

        var result = await client.GetMetadataBatchAsync(["123", "invalid", "123", "456"]);

        var metadata = Assert.Single(result).Value;
        Assert.Equal("123", metadata.WorkshopId);
        Assert.Equal("A Mod", metadata.Title);
        Assert.Equal(4096, metadata.FileSize);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1_800_000_000), metadata.LatestRemoteUpdatedAtUtc);
        Assert.NotNull(requestBody);
        Assert.Contains("itemcount=2", requestBody, StringComparison.Ordinal);
        Assert.Contains("publishedfileids%5B0%5D=123", requestBody, StringComparison.Ordinal);
        Assert.Contains("publishedfileids%5B1%5D=456", requestBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RequestsAtMostOneHundredIdsPerBatchAndTwoBatchesConcurrently()
    {
        var requestSizes = new ConcurrentBag<int>();
        var currentConcurrency = 0;
        var maximumConcurrency = 0;
        using var handler = new StubHttpMessageHandler(async (request, cancellationToken) =>
        {
            var concurrency = Interlocked.Increment(ref currentConcurrency);
            SetMaximum(ref maximumConcurrency, concurrency);
            try
            {
                var body = await request.Content!.ReadAsStringAsync(cancellationToken);
                var values = ParseForm(body);
                var ids = values
                    .Where(pair => pair.Key.StartsWith("publishedfileids[", StringComparison.Ordinal))
                    .Select(pair => pair.Value)
                    .ToArray();
                requestSizes.Add(ids.Length);
                await Task.Delay(30, cancellationToken);
                var details = ids.Select(id => new
                {
                    publishedfileid = id,
                    result = 1,
                    consumer_app_id = 281990,
                    time_updated = 1_800_000_000,
                });
                return StubHttpMessageHandler.Response(
                    HttpStatusCode.OK,
                    JsonContent.Create(new { response = new { publishedfiledetails = details } }));
            }
            finally
            {
                Interlocked.Decrement(ref currentConcurrency);
            }
        });
        using var httpClient = new HttpClient(handler);
        var client = new WorkshopClient(httpClient);
        var ids = Enumerable.Range(100_000, 205)
            .Select(value => value.ToString(CultureInfo.InvariantCulture))
            .ToArray();

        var result = await client.GetMetadataBatchAsync(ids);

        Assert.Equal(205, result.Count);
        Assert.Equal([5, 100, 100], requestSizes.Order());
        Assert.Equal(2, maximumConcurrency);
    }

    [Fact]
    public async Task FailedBatchDoesNotDiscardSuccessfulBatchMetadata()
    {
        using var handler = new StubHttpMessageHandler(async (request, cancellationToken) =>
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            var ids = ParseForm(body)
                .Where(pair => pair.Key.StartsWith("publishedfileids[", StringComparison.Ordinal))
                .Select(pair => pair.Value)
                .ToArray();
            if (ids.Length == WorkshopClient.MaximumBatchSize)
            {
                return StubHttpMessageHandler.Response(HttpStatusCode.ServiceUnavailable);
            }

            return StubHttpMessageHandler.Response(
                HttpStatusCode.OK,
                JsonContent.Create(new
                {
                    response = new
                    {
                        publishedfiledetails = ids.Select(id => new
                        {
                            publishedfileid = id,
                            result = 1,
                            time_updated = 1_800_000_000,
                        }),
                    },
                }));
        });
        using var httpClient = new HttpClient(handler);
        var client = new WorkshopClient(httpClient);
        var ids = Enumerable.Range(100_000, 101)
            .Select(value => value.ToString(CultureInfo.InvariantCulture))
            .ToArray();

        var result = await client.GetMetadataBatchAsync(ids);

        Assert.Single(result);
    }

    [Fact]
    public async Task CancellationIsNotConvertedIntoMissingMetadata()
    {
        using var handler = new StubHttpMessageHandler(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return StubHttpMessageHandler.Response(HttpStatusCode.OK);
        });
        using var httpClient = new HttpClient(handler);
        var client = new WorkshopClient(httpClient);
        using var cancellationSource = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.GetMetadataBatchAsync(["123"], cancellationToken: cancellationSource.Token));
    }

    private static Dictionary<string, string> ParseForm(string body)
    {
        return body.Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split('=', count: 2))
            .ToDictionary(
                pair => Uri.UnescapeDataString(pair[0]),
                pair => Uri.UnescapeDataString(pair[1]),
                StringComparer.Ordinal);
    }

    private static void SetMaximum(ref int location, int value)
    {
        var current = Volatile.Read(ref location);
        while (value > current)
        {
            var observed = Interlocked.CompareExchange(ref location, value, current);
            if (observed == current)
            {
                return;
            }

            current = observed;
        }
    }
}
