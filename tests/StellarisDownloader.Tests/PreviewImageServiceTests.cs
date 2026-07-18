using System.Net;
using System.Net.Http;
using StellarisDownloader.App.Services;

namespace StellarisDownloader.Tests;

public sealed class PreviewImageServiceTests
{
    private static readonly byte[] OnePixelPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Y9Zl1sAAAAASUVORK5CYII=");

    [Fact]
    public async Task SuccessfulImageIsFrozenAndReusedFromMemoryCache()
    {
        var handler = new CountingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(OnePixelPng),
        });
        using var client = new HttpClient(handler);
        var service = new PreviewImageService(client);

        var first = await service.LoadAsync("https://example.test/preview.png");
        var second = await service.LoadAsync("https://example.test/preview.png");

        Assert.NotNull(first);
        Assert.True(first.IsFrozen);
        Assert.Same(first, second);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task InvalidUrlAndOversizedResponsesReturnNoImage()
    {
        var handler = new CountingHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([]),
            };
            response.Content.Headers.ContentLength = 20L * 1024 * 1024 + 1;
            return response;
        });
        using var client = new HttpClient(handler);
        var service = new PreviewImageService(client);

        var invalid = await service.LoadAsync("file:///C:/private/image.png");
        var oversized = await service.LoadAsync("https://example.test/large.png");

        Assert.Null(invalid);
        Assert.Null(oversized);
        Assert.Equal(1, handler.CallCount);
    }

    private sealed class CountingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> responseFactory;

        public CountingHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
        {
            this.responseFactory = responseFactory;
        }

        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            return Task.FromResult(responseFactory(request));
        }
    }
}
