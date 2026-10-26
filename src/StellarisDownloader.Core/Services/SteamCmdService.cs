using System.ComponentModel;
using System.IO.Compression;
using StellarisDownloader.Core.Models;

namespace StellarisDownloader.Core.Services;

public sealed class SteamCmdService : ISteamCmdService, IDisposable
{
    public const int StellarisAppId = 281990;

    private const int SelfUpdateRestartExitCode = 7;
    private const int MaximumVerificationAttempts = 3;

    private static readonly Uri DefaultDownloadUri = new(
        "https://steamcdn-a.akamaihd.net/client/installer/steamcmd.zip");

    private readonly HttpClient httpClient;
    private readonly IProcessRunner processRunner;
    private readonly string steamCmdDirectory;
    private readonly string executablePath;
    private readonly Uri downloadUri;
    private readonly SemaphoreSlim installationGate = new(1, 1);

    public SteamCmdService(
        HttpClient httpClient,
        IProcessRunner processRunner,
        string steamCmdDirectory,
        Uri? downloadUri = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(processRunner);
        ArgumentException.ThrowIfNullOrWhiteSpace(steamCmdDirectory);

        this.httpClient = httpClient;
        this.processRunner = processRunner;
        this.steamCmdDirectory = NormalizePath(steamCmdDirectory);
        executablePath = Path.Combine(this.steamCmdDirectory, "steamcmd.exe");
        this.downloadUri = downloadUri ?? DefaultDownloadUri;
    }

    public async Task<SteamCmdInstallationResult> EnsureInstalledAsync(
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await installationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new SteamCmdInstallationResult(
                OperationStatus.Cancelled,
                executablePath,
                InstalledNow: false,
                "SteamCMD installation was cancelled.");
        }

        try
        {
            if (File.Exists(executablePath))
            {
                return new SteamCmdInstallationResult(
                    OperationStatus.Succeeded,
                    executablePath,
                    InstalledNow: false,
                    Error: null);
            }

            return await InstallAsync(progress, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            installationGate.Release();
        }
    }

    public const int MaximumItemsPerProcess = 25;
    public static readonly TimeSpan DownloadIdleTimeout = TimeSpan.FromMinutes(10);

    public async Task<DownloadResult> DownloadAsync(
        DownloadRequest request,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var results = await DownloadCoreAsync([request], retryUnknown: false, progress, cancellationToken)
            .ConfigureAwait(false);
        return results[0];
    }

    public Task<IReadOnlyList<DownloadResult>> DownloadBatchAsync(
        IReadOnlyList<DownloadRequest> requests,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        DownloadCoreAsync(requests, retryUnknown: true, progress, cancellationToken);

    private async Task<IReadOnlyList<DownloadResult>> DownloadCoreAsync(
        IReadOnlyList<DownloadRequest> requests,
        bool retryUnknown,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requests);
        var results = new List<DownloadResult>(requests.Count);
        var completed = new HashSet<int>();
        var cancelled = false;
        foreach (var chunk in requests.Chunk(MaximumItemsPerProcess))
        {
            var offset = results.Count;
            progress?.Report(new OperationProgress(
                "DownloadingQueue", completed.Count, requests.Count, chunk[0].WorkshopId,
                $"Downloading queue item {offset + 1} of {requests.Count}."));
            void Report(int index, DownloadResult result)
            {
                completed.Add(offset + index);
                progress?.Report(new OperationProgress(
                    "DownloadItemCompleted", completed.Count, requests.Count,
                    result.WorkshopId, $"{result.WorkshopId}: {result.Status}")
                {
                    ItemResult = result,
                });
            }

            var (chunkResults, unknown) = await RunChunkAsync(
                chunk, Report, progress, cancelled ? new CancellationToken(true) : cancellationToken)
                .ConfigureAwait(false);
            foreach (var index in unknown)
            {
                if (retryUnknown)
                {
                    var (retryResults, _) = await RunChunkAsync(
                        [chunk[index]], (_, result) => Report(index, result), progress,
                        cancelled ? new CancellationToken(true) : cancellationToken).ConfigureAwait(false);
                    chunkResults[index] = retryResults[0];
                    cancelled |= chunkResults[index].Status == OperationStatus.Cancelled;
                }

                if (!completed.Contains(offset + index))
                {
                    Report(index, chunkResults[index]);
                }
            }

            results.AddRange(chunkResults);
            cancelled |= chunkResults.Any(result => result.Status == OperationStatus.Cancelled);
        }

        return results;
    }

    private async Task<(DownloadResult[] Results, List<int> Unknown)> RunChunkAsync(
        DownloadRequest[] requests,
        Action<int, DownloadResult> report,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        var results = new DownloadResult[requests.Length];
        var pending = new List<int>();
        var unknown = new List<int>();
        for (var index = 0; index < requests.Length; index++)
        {
            var request = requests[index];
            ArgumentNullException.ThrowIfNull(request);
            var root = NormalizePath(request.LibraryRoot);
            if (!IsValidWorkshopId(request.WorkshopId))
            {
                results[index] = FailedDownload(request.WorkshopId, root, "Workshop ID must contain only ASCII digits.");
                report(index, results[index]);
            }
            else
            {
                pending.Add(index);
            }
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (pending.Count == 0)
            {
                return (results, unknown);
            }

            var installation = await EnsureInstalledAsync(progress, cancellationToken).ConfigureAwait(false);
            if (installation.Status != OperationStatus.Succeeded)
            {
                CompletePending(installation.Status, installation.Error);
                return (results, unknown);
            }

            var arguments = new List<string> { "+login", "anonymous" };
            foreach (var index in pending)
            {
                arguments.AddRange(["+workshop_download_item", "281990", requests[index].WorkshopId]);
            }
            arguments.Add("+quit");
            var processResult = await processRunner.RunAsync(
                executablePath, arguments, steamCmdDirectory, DownloadIdleTimeout, progress,
                outputReceived: line =>
                {
                    foreach (var index in pending)
                    {
                        var state = SteamCmdOutputClassifier.Classify(requests[index].WorkshopId, [line]);
                        if (state != SteamCmdTerminalState.Unknown)
                        {
                            results[index] = BuildDownloadResult(requests[index], state, [line.Text]);
                            report(index, results[index]);
                        }
                    }
                },
                idleTimeout: true, cancellationToken: cancellationToken).ConfigureAwait(false);
            var output = processResult.Output.OrderBy(line => line.Sequence).Select(line => line.Text).ToArray();
            foreach (var index in pending)
            {
                var request = requests[index];
                var state = SteamCmdOutputClassifier.Classify(request.WorkshopId, processResult.Output);
                var alreadyReported = results[index] is not null;
                var result = results[index] ?? BuildDownloadResult(request, state, output);
                if (state == SteamCmdTerminalState.Unknown && !alreadyReported)
                {
                    if (processResult.Cancelled || processResult.TimedOut)
                    {
                        result = result with
                        {
                            Status = processResult.Cancelled ? OperationStatus.Cancelled : OperationStatus.Failed,
                            TimedOut = !processResult.Cancelled && processResult.TimedOut,
                            Error = processResult.Cancelled
                                ? "SteamCMD download was cancelled."
                                : $"SteamCMD timed out after {DownloadIdleTimeout} without output.",
                        };
                    }
                    else
                    {
                        unknown.Add(index);
                    }
                }

                results[index] = result with { ExitCode = processResult.ExitCode, OutputLines = output };
                if (!alreadyReported && !unknown.Contains(index))
                {
                    report(index, results[index]);
                }
            }
        }
        catch (OperationCanceledException)
        {
            CompletePending(OperationStatus.Cancelled, "SteamCMD download was cancelled.");
        }
        catch (Exception exception) when (IsOperationalFailure(exception))
        {
            CompletePending(OperationStatus.Failed, exception.Message);
        }

        return (results, unknown);

        void CompletePending(OperationStatus status, string? error)
        {
            foreach (var index in pending.Where(index => results[index] is null))
            {
                var request = requests[index];
                results[index] = new DownloadResult
                {
                    WorkshopId = request.WorkshopId,
                    Status = status,
                    ContentPath = Path.Combine(NormalizePath(request.LibraryRoot), request.WorkshopId),
                    Error = error,
                };
                report(index, results[index]);
            }
        }
    }

    private static DownloadResult BuildDownloadResult(
        DownloadRequest request,
        SteamCmdTerminalState terminalState,
        IReadOnlyList<string> outputLines)
    {
        var contentPath = Path.Combine(NormalizePath(request.LibraryRoot), request.WorkshopId);
        var folderExists = Directory.Exists(contentPath);
        var folderNonEmpty = folderExists && Directory.EnumerateFileSystemEntries(contentPath).Any();
        var succeeded = terminalState == SteamCmdTerminalState.Succeeded && folderNonEmpty;
        return new DownloadResult
        {
            WorkshopId = request.WorkshopId,
            Status = succeeded ? OperationStatus.Succeeded : OperationStatus.Failed,
            ContentPath = contentPath,
            FolderExists = folderExists,
            FolderNonEmpty = folderNonEmpty,
            OutputLines = outputLines,
            Error = succeeded ? null : BuildDownloadError(terminalState, folderExists, folderNonEmpty, outputLines),
        };
    }

    public void Dispose()
    {
        installationGate.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task<SteamCmdInstallationResult> InstallAsync(
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(steamCmdDirectory);
        var token = Guid.NewGuid().ToString("N");
        var archivePath = Path.Combine(steamCmdDirectory, $".install-{token}.zip");
        var extractionPath = Path.Combine(steamCmdDirectory, $".install-{token}");
        var stagedExecutablePath = Path.Combine(steamCmdDirectory, $".steamcmd-{token}.exe");

        try
        {
            progress?.Report(new OperationProgress(
                "InstallingSteamCmd",
                Completed: 0,
                Total: 2,
                WorkshopId: null,
                Message: "Downloading SteamCMD for Windows."));
            using var response = await httpClient.GetAsync(
                downloadUri,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using (var archiveStream = new FileStream(
                archivePath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 81920,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await response.Content.CopyToAsync(archiveStream, cancellationToken).ConfigureAwait(false);
                await archiveStream.FlushAsync(cancellationToken).ConfigureAwait(false);
                archiveStream.Flush(flushToDisk: true);
            }

            Directory.CreateDirectory(extractionPath);
            ZipFile.ExtractToDirectory(archivePath, extractionPath, overwriteFiles: false);
            var extractedExecutable = Directory
                .EnumerateFiles(extractionPath, "steamcmd.exe", SearchOption.AllDirectories)
                .SingleOrDefault();
            if (extractedExecutable is null)
            {
                throw new InvalidDataException("The SteamCMD archive did not contain steamcmd.exe.");
            }

            File.Move(extractedExecutable, stagedExecutablePath);
            File.Move(stagedExecutablePath, executablePath, overwrite: true);
            progress?.Report(new OperationProgress(
                "InstallingSteamCmd",
                Completed: 1,
                Total: 2,
                WorkshopId: null,
                Message: "Verifying steamcmd.exe can start."));
            ProcessRunResult verification;
            var verificationAttempt = 0;
            while (true)
            {
                verificationAttempt++;
                verification = await processRunner.RunAsync(
                    executablePath,
                    ["+quit"],
                    steamCmdDirectory,
                    TimeSpan.FromMinutes(5),
                    progress,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                var executableExists = File.Exists(executablePath);
                if (!verification.Cancelled
                    && !verification.TimedOut
                    && verification.ExitCode == 0
                    && executableExists)
                {
                    break;
                }

                var canRetryAfterSelfUpdate = !verification.Cancelled
                    && !verification.TimedOut
                    && verification.ExitCode == SelfUpdateRestartExitCode
                    && executableExists
                    && verificationAttempt < MaximumVerificationAttempts;
                if (!canRetryAfterSelfUpdate)
                {
                    File.Delete(executablePath);
                    var reason = verification.Cancelled
                        ? "SteamCMD verification was cancelled."
                        : verification.TimedOut
                            ? "SteamCMD verification timed out."
                            : !executableExists
                                ? "steamcmd.exe was missing after verification."
                                : verification.ExitCode == SelfUpdateRestartExitCode
                                    ? $"SteamCMD self-update did not settle after {MaximumVerificationAttempts} verification attempts."
                                    : verification.ExitCode is int exitCode
                                        ? $"steamcmd.exe exited with code {exitCode}."
                                        : "steamcmd.exe exited without an exit code.";
                    return new SteamCmdInstallationResult(
                        verification.Cancelled ? OperationStatus.Cancelled : OperationStatus.Failed,
                        executablePath,
                        InstalledNow: false,
                        reason);
                }

                progress?.Report(new OperationProgress(
                    "InstallingSteamCmd",
                    Completed: 1,
                    Total: 2,
                    WorkshopId: null,
                    Message: $"SteamCMD updated itself; retrying verification ({verificationAttempt + 1}/{MaximumVerificationAttempts})."));
            }

            progress?.Report(new OperationProgress(
                "InstallingSteamCmd",
                Completed: 2,
                Total: 2,
                WorkshopId: null,
                Message: "SteamCMD installation is ready."));
            return new SteamCmdInstallationResult(
                OperationStatus.Succeeded,
                executablePath,
                InstalledNow: true,
                Error: null);
        }
        catch (OperationCanceledException)
        {
            File.Delete(executablePath);
            return new SteamCmdInstallationResult(
                OperationStatus.Cancelled,
                executablePath,
                InstalledNow: false,
                "SteamCMD installation was cancelled.");
        }
        catch (Exception exception) when (IsOperationalFailure(exception))
        {
            File.Delete(executablePath);
            return new SteamCmdInstallationResult(
                OperationStatus.Failed,
                executablePath,
                InstalledNow: false,
                exception.Message);
        }
        finally
        {
            TryDeleteTemporaryFile(stagedExecutablePath);
            TryDeleteTemporaryFile(archivePath);
            TryDeleteTemporaryDirectory(extractionPath);
        }
    }

    private void TryDeleteTemporaryDirectory(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        var normalizedPath = NormalizePath(path);
        var parent = Path.GetDirectoryName(normalizedPath);
        if (!string.Equals(parent, steamCmdDirectory, StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(normalizedPath).StartsWith(".install-", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Refusing to delete unexpected temporary path: {path}");
        }

        try
        {
            Directory.Delete(normalizedPath, recursive: true);
        }
        catch (IOException)
        {
            // Installation result takes precedence; a later retry can remove this private temp directory.
        }
        catch (UnauthorizedAccessException)
        {
            // Installation result takes precedence; a later retry can remove this private temp directory.
        }
    }

    private void TryDeleteTemporaryFile(string path)
    {
        if (!File.Exists(path))
        {
            return;
        }

        var normalizedPath = NormalizePath(path);
        var parent = Path.GetDirectoryName(normalizedPath);
        var fileName = Path.GetFileName(normalizedPath);
        if (!string.Equals(parent, steamCmdDirectory, StringComparison.OrdinalIgnoreCase)
            || (!fileName.StartsWith(".install-", StringComparison.Ordinal)
                && !fileName.StartsWith(".steamcmd-", StringComparison.Ordinal)))
        {
            throw new InvalidOperationException($"Refusing to delete unexpected temporary file: {path}");
        }

        try
        {
            File.Delete(normalizedPath);
        }
        catch (IOException)
        {
            // Installation result takes precedence; a later retry can remove this private temp file.
        }
        catch (UnauthorizedAccessException)
        {
            // Installation result takes precedence; a later retry can remove this private temp file.
        }
    }

    private static string BuildDownloadError(
        SteamCmdTerminalState terminalState,
        bool folderExists,
        bool folderNonEmpty,
        IReadOnlyList<string> outputLines)
    {
        if (terminalState == SteamCmdTerminalState.Failed)
        {
            return outputLines.LastOrDefault(line => line.Contains("failed", StringComparison.OrdinalIgnoreCase))
                ?? "SteamCMD reported an explicit download failure.";
        }

        if (terminalState == SteamCmdTerminalState.Unknown)
        {
            return "SteamCMD output did not contain a recognized terminal result for this Workshop ID.";
        }

        if (!folderExists)
        {
            return "SteamCMD reported success, but the Workshop directory does not exist.";
        }

        if (!folderNonEmpty)
        {
            return "SteamCMD reported success, but the Workshop directory is empty.";
        }

        return "SteamCMD download verification failed.";
    }

    private static DownloadResult FailedDownload(
        string workshopId,
        string contentPath,
        string error)
    {
        return new DownloadResult
        {
            WorkshopId = workshopId,
            Status = OperationStatus.Failed,
            ContentPath = contentPath,
            Error = error,
        };
    }

    private static bool IsOperationalFailure(Exception exception) =>
        exception is HttpRequestException
            or IOException
            or InvalidDataException
            or UnauthorizedAccessException
            or InvalidOperationException
            or Win32Exception
            or NotSupportedException;

    private static bool IsValidWorkshopId(string workshopId) =>
        !string.IsNullOrWhiteSpace(workshopId) && workshopId.All(char.IsAsciiDigit);

    private static string NormalizePath(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
}
