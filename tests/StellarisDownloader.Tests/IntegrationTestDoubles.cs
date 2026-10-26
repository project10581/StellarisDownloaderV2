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

    public List<IReadOnlyList<string>> Calls { get; } = [];

    public Action<ProcessOutputLine>? AfterOutput { get; set; }

    public TimeSpan LastTimeout { get; private set; }

    public bool LastIdleTimeout { get; private set; }

    public Task<ProcessRunResult> RunAsync(
        string executablePath,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        TimeSpan timeout,
        IProgress<OperationProgress>? progress = null,
        Action<ProcessOutputLine>? outputReceived = null,
        bool idleTimeout = false,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CallCount++;
        LastArguments = arguments;
        Calls.Add(arguments.ToArray());
        LastTimeout = timeout;
        LastIdleTimeout = idleTimeout;
        var result = resultFactory(executablePath, arguments);
        foreach (var line in result.Output.OrderBy(line => line.Sequence))
        {
            outputReceived?.Invoke(line);
            AfterOutput?.Invoke(line);
        }

        return Task.FromResult(result);
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

    public int BatchCallCount { get; private set; }

    public async Task<IReadOnlyList<DownloadResult>> DownloadBatchAsync(
        IReadOnlyList<DownloadRequest> requests,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        BatchCallCount++;
        var results = new List<DownloadResult>();
        var cancelled = false;
        foreach (var request in requests)
        {
            DownloadResult result;
            try
            {
                if (cancelled) { throw new OperationCanceledException(); }
                result = await DownloadAsync(request, progress, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                result = new DownloadResult
                {
                    WorkshopId = request.WorkshopId,
                    ContentPath = Path.Combine(request.LibraryRoot, request.WorkshopId),
                    Status = OperationStatus.Cancelled,
                };
            }
            catch (IOException exception)
            {
                result = new DownloadResult
                {
                    WorkshopId = request.WorkshopId,
                    ContentPath = Path.Combine(request.LibraryRoot, request.WorkshopId),
                    Status = OperationStatus.Failed,
                    Error = exception.Message,
                };
            }

            results.Add(result);
            cancelled |= result.Status == OperationStatus.Cancelled;
            progress?.Report(new OperationProgress(
                "DownloadItemCompleted", results.Count, requests.Count, request.WorkshopId, result.Status.ToString())
            {
                ItemResult = result,
            });
        }

        return results;
    }

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

    public int CallCount { get; private set; }

    public Task<IReadOnlyDictionary<string, WorkshopMetadata>> GetMetadataBatchAsync(
        IReadOnlyCollection<string> workshopIds,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CallCount++;
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

internal sealed class ManualTimeProvider : TimeProvider, IDisposable
{
    private TimeSpan elapsed;
    private ManualTimer? timer;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        timer = new ManualTimer(this, callback, state);
        timer.Change(dueTime, period);
        return timer;
    }

    public void Advance(TimeSpan duration)
    {
        elapsed += duration;
        timer?.Tick();
    }

    public void Dispose() => timer?.Dispose();

    private sealed class ManualTimer(ManualTimeProvider clock, TimerCallback callback, object? state) : ITimer
    {
        private TimeSpan? due;
        private TimeSpan period;

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            due = dueTime == Timeout.InfiniteTimeSpan ? null : clock.elapsed + dueTime;
            this.period = period;
            return true;
        }

        public void Tick()
        {
            if (due is { } deadline && clock.elapsed >= deadline)
            {
                due = period == Timeout.InfiniteTimeSpan ? null : clock.elapsed + period;
                callback(state);
            }
        }

        public void Dispose() => due = null;
        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
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
