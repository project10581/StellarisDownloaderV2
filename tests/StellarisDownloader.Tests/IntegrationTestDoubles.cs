using System.Net;
using StellarisDownloader.Core.Models;
using StellarisDownloader.Core.Services;

namespace StellarisDownloader.Tests;

internal sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler;

    public StubHttpMessageHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
    {
        this.handler = handler;
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken) =>
        handler(request, cancellationToken);

    public static HttpResponseMessage Response(
        HttpStatusCode statusCode,
        HttpContent? content = null) =>
        new(statusCode) { Content = content };
}

internal sealed class StubProcessRunner : IProcessRunner
{
    private readonly Func<string, IReadOnlyList<string>, ProcessRunResult> resultFactory;

    public StubProcessRunner(ProcessRunResult result)
        : this((_, _) => result)
    {
    }

    public StubProcessRunner(Func<string, IReadOnlyList<string>, ProcessRunResult> resultFactory)
    {
        this.resultFactory = resultFactory;
    }

    public int CallCount { get; private set; }

    public IReadOnlyList<string>? LastArguments { get; private set; }

    public Task<ProcessRunResult> RunAsync(
        string executablePath,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        TimeSpan timeout,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CallCount++;
        LastArguments = arguments;
        return Task.FromResult(resultFactory(executablePath, arguments));
    }
}
