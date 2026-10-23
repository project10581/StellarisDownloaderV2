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
            if (request.Method == HttpMethod.Get)
            {
                Assert.Contains("steamids=42", request.RequestUri!.Query, StringComparison.Ordinal);
                return StubHttpMessageHandler.Response(
                    HttpStatusCode.OK,
                    JsonContent.Create(new[]
                    {
                        new
                        {
                            steamid = "42",
                            persona_name = "Real Author",
                        },
                    }));
            }

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
        using var client = new WorkshopClient(httpClient);

        var result = await client.GetMetadataBatchAsync(["123", "invalid", "123", "456"]);

        var metadata = Assert.Single(result).Value;
        Assert.Equal("123", metadata.WorkshopId);
        Assert.Equal("A Mod", metadata.Title);
        Assert.Equal("42", metadata.CreatorId);
        Assert.Equal("Real Author", metadata.CreatorName);
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
        using var client = new WorkshopClient(httpClient);
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
        using var client = new WorkshopClient(httpClient);
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
        using var client = new WorkshopClient(httpClient);
        using var cancellationSource = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.GetMetadataBatchAsync(["123"], cancellationToken: cancellationSource.Token));
    }

    [Fact]
    public async Task AuthorLookupDeduplicatesAndCachesCreatorNames()
    {
        var authorRequestCount = 0;
        using var handler = new StubHttpMessageHandler((request, _) =>
        {
            if (request.Method == HttpMethod.Get)
            {
                authorRequestCount++;
                Assert.Equal("?steamids=76561198000000001", request.RequestUri!.Query);
                return Task.FromResult(StubHttpMessageHandler.Response(
                    HttpStatusCode.OK,
                    JsonContent.Create(new[]
                    {
                        new
                        {
                            steamid = "76561198000000001",
                            persona_name = "Shared Author",
                        },
                    })));
            }

            return Task.FromResult(StubHttpMessageHandler.Response(
                HttpStatusCode.OK,
                JsonContent.Create(new
                {
                    response = new
                    {
                        publishedfiledetails = new[]
                        {
                            new
                            {
                                publishedfileid = "100",
                                result = 1,
                                creator = "76561198000000001",
                            },
                            new
                            {
                                publishedfileid = "200",
                                result = 1,
                                creator = "76561198000000001",
                            },
                        },
                    },
                })));
        });
        using var httpClient = new HttpClient(handler);
        using var client = new WorkshopClient(httpClient);

        var first = await client.GetMetadataBatchAsync(["100", "200"]);
        var second = await client.GetMetadataBatchAsync(["100"]);

        Assert.Equal(2, first.Count);
        Assert.All(first.Values, item => Assert.Equal("Shared Author", item.CreatorName));
        Assert.Equal("Shared Author", second["100"].CreatorName);
        Assert.Equal(1, authorRequestCount);
    }

    [Fact]
    public async Task AuthorLookupChunksOverTwoHundredCreatorsAcrossMultipleBatches()
    {
        var personaBatchSizes = new List<int>();
        using var handler = new StubHttpMessageHandler(async (request, cancellationToken) =>
        {
            if (request.Method == HttpMethod.Get)
            {
                var query = request.RequestUri!.Query.TrimStart('?');
                var rawIds = Uri.UnescapeDataString(query["steamids=".Length..])
                    .Split(',', StringSplitOptions.RemoveEmptyEntries);
                personaBatchSizes.Add(rawIds.Length);
                return StubHttpMessageHandler.Response(
                    HttpStatusCode.OK,
                    JsonContent.Create(rawIds.Select(id => new
                    {
                        steamid = id,
                        persona_name = $"Author {id}",
                    })));
            }

            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            var ids = ParseForm(body)
                .Where(pair => pair.Key.StartsWith("publishedfileids[", StringComparison.Ordinal))
                .Select(pair => pair.Value)
                .ToArray();
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
                            creator = $"76561198{id}",
                        }),
                    },
                }));
        });
        using var httpClient = new HttpClient(handler);
        using var client = new WorkshopClient(httpClient);
        var workshopIds = Enumerable.Range(100_000, 205)
            .Select(id => id.ToString(CultureInfo.InvariantCulture))
            .ToArray();

        var result = await client.GetMetadataBatchAsync(workshopIds);

        Assert.Equal(205, result.Count);
        Assert.Equal([200, 5], personaBatchSizes);
        Assert.All(result.Values, item => Assert.NotNull(item.CreatorName));
    }

    [Fact]
    public async Task AuthorLookupFailurePreservesMetadataAndRecoversAfterCooldown()
    {
        var authorRequestCount = 0;
        var currentTime = new DateTimeOffset(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);
        var timeProvider = new MutableTimeProvider(currentTime);
        using var handler = new StubHttpMessageHandler((request, _) =>
        {
            if (request.Method == HttpMethod.Get)
            {
                authorRequestCount++;
                if (authorRequestCount == 1)
                {
                    return Task.FromResult(StubHttpMessageHandler.Response(HttpStatusCode.TooManyRequests));
                }

                return Task.FromResult(StubHttpMessageHandler.Response(
                    HttpStatusCode.OK,
                    JsonContent.Create(new[]
                    {
                        new
                        {
                            steamid = "76561198000000002",
                            persona_name = "Recovered Author",
                        },
                    })));
            }

            return Task.FromResult(StubHttpMessageHandler.Response(
                HttpStatusCode.OK,
                JsonContent.Create(new
                {
                    response = new
                    {
                        publishedfiledetails = new[]
                        {
                            new
                            {
                                publishedfileid = "100",
                                result = 1,
                                title = "Metadata survives",
                                creator = "76561198000000002",
                                file_size = "2048",
                            },
                        },
                    },
                })));
        });
        using var httpClient = new HttpClient(handler);
        using var client = new WorkshopClient(httpClient, timeProvider: timeProvider);

        var first = await client.GetMetadataBatchAsync(["100"]);
        var second = await client.GetMetadataBatchAsync(["100"]);
        timeProvider.Advance(WorkshopClient.PersonaLookupCooldown);
        var third = await client.GetMetadataBatchAsync(["100"]);

        Assert.Equal("Metadata survives", first["100"].Title);
        Assert.Equal("76561198000000002", first["100"].CreatorId);
        Assert.Null(first["100"].CreatorName);
        Assert.Equal(2048, first["100"].FileSize);
        Assert.Null(second["100"].CreatorName);
        Assert.Equal("Recovered Author", third["100"].CreatorName);
        Assert.Equal(2, authorRequestCount);
    }

    [Fact]
    public async Task CancellationDuringAuthorLookupIsPropagated()
    {
        using var handler = new StubHttpMessageHandler(async (request, cancellationToken) =>
        {
            if (request.Method == HttpMethod.Get)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            return StubHttpMessageHandler.Response(
                HttpStatusCode.OK,
                JsonContent.Create(new
                {
                    response = new
                    {
                        publishedfiledetails = new[]
                        {
                            new
                            {
                                publishedfileid = "100",
                                result = 1,
                                creator = "76561198000000003",
                            },
                        },
                    },
                }));
        });
        using var httpClient = new HttpClient(handler);
        using var client = new WorkshopClient(httpClient);
        using var cancellationSource = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.GetMetadataBatchAsync(
                ["100"],
                cancellationToken: cancellationSource.Token));
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

    private sealed class MutableTimeProvider(DateTimeOffset initialUtcNow) : TimeProvider
    {
        private DateTimeOffset utcNow = initialUtcNow;

        public override DateTimeOffset GetUtcNow() => utcNow;

        public void Advance(TimeSpan delta) => utcNow = utcNow.Add(delta);
    }
}
