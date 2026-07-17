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

internal sealed class StubSteamCmdService : ISteamCmdService
{
    private readonly Func<DownloadRequest, CancellationToken, Task<DownloadResult>> download;

    public StubSteamCmdService(
        Func<DownloadRequest, CancellationToken, Task<DownloadResult>> download)
    {
        this.download = download;
    }

    public List<string> DownloadedIds { get; } = [];

    public Task<SteamCmdInstallationResult> EnsureInstalledAsync(
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new SteamCmdInstallationResult(
            OperationStatus.Succeeded,
            "steamcmd.exe",
            InstalledNow: false,
            Error: null));

    public Task<DownloadResult> DownloadAsync(
        DownloadRequest request,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        DownloadedIds.Add(request.WorkshopId);
        return download(request, cancellationToken);
    }
}

internal sealed class StubWorkshopClient : IWorkshopClient
{
    private readonly IReadOnlyDictionary<string, WorkshopMetadata> metadata;

    public StubWorkshopClient(IReadOnlyDictionary<string, WorkshopMetadata>? metadata = null)
    {
        this.metadata = metadata
            ?? new Dictionary<string, WorkshopMetadata>(StringComparer.Ordinal);
    }

    public IReadOnlyList<string> LastRequestedIds { get; private set; } = [];

    public Task<IReadOnlyDictionary<string, WorkshopMetadata>> GetMetadataBatchAsync(
        IReadOnlyCollection<string> workshopIds,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LastRequestedIds = workshopIds.ToArray();
        return Task.FromResult(metadata);
    }
}

internal sealed class FixedTimeProvider : TimeProvider
{
    private readonly DateTimeOffset utcNow;

    public FixedTimeProvider(DateTimeOffset utcNow)
    {
        this.utcNow = utcNow;
    }

    public override DateTimeOffset GetUtcNow() => utcNow;
}

internal sealed class StubFileDeletionService : IFileDeletionService
{
    public bool FailRecycle { get; set; }

    public bool FailPermanent { get; set; }

    public int RecycleCallCount { get; private set; }

    public int PermanentCallCount { get; private set; }

    public Task SendDirectoryToRecycleBinAsync(
        string directoryPath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RecycleCallCount++;
        if (FailRecycle)
        {
            throw new IOException("Forced Recycle Bin failure.");
        }

        DeleteIfPresent(directoryPath);
        return Task.CompletedTask;
    }

    public Task DeleteDirectoryPermanentlyAsync(
        string directoryPath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        PermanentCallCount++;
        if (FailPermanent)
        {
            throw new IOException("Forced permanent deletion failure.");
        }

        DeleteIfPresent(directoryPath);
        return Task.CompletedTask;
    }

    private static void DeleteIfPresent(string directoryPath)
    {
        if (Directory.Exists(directoryPath))
        {
            Directory.Delete(directoryPath, recursive: true);
        }
    }
}
