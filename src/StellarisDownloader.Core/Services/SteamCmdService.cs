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

    public async Task<DownloadResult> DownloadAsync(
        DownloadRequest request,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var normalizedRoot = NormalizePath(request.LibraryRoot);
        if (!IsValidWorkshopId(request.WorkshopId))
        {
            return FailedDownload(
                request.WorkshopId,
                normalizedRoot,
                "Workshop ID must contain only ASCII digits.");
        }

        var contentPath = Path.Combine(normalizedRoot, request.WorkshopId);

        if (request.Timeout <= TimeSpan.Zero)
        {
            return FailedDownload(request.WorkshopId, contentPath, "Download timeout must be positive.");
        }

        var installation = await EnsureInstalledAsync(progress, cancellationToken).ConfigureAwait(false);
        if (installation.Status != OperationStatus.Succeeded)
        {
            return new DownloadResult
            {
                WorkshopId = request.WorkshopId,
                Status = installation.Status,
                ContentPath = contentPath,
                Error = installation.Error,
            };
        }

        try
        {
            progress?.Report(new OperationProgress(
                "DownloadingWorkshopItem",
                Completed: 0,
                Total: 1,
                request.WorkshopId,
                $"Starting SteamCMD download for Workshop item {request.WorkshopId}."));
            var processResult = await processRunner.RunAsync(
                executablePath,
                [
                    "+login",
                    "anonymous",
                    "+workshop_download_item",
                    StellarisAppId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    request.WorkshopId,
                    "+quit",
                ],
                steamCmdDirectory,
                request.Timeout,
                progress,
                cancellationToken).ConfigureAwait(false);
            var outputLines = processResult.Output
                .OrderBy(line => line.Sequence)
                .Select(line => line.Text)
                .ToArray();
            var folderExists = Directory.Exists(contentPath);
            var folderNonEmpty = folderExists && Directory.EnumerateFileSystemEntries(contentPath).Any();

            if (processResult.Cancelled)
            {
                return new DownloadResult
                {
                    WorkshopId = request.WorkshopId,
                    Status = OperationStatus.Cancelled,
                    ContentPath = contentPath,
                    FolderExists = folderExists,
                    FolderNonEmpty = folderNonEmpty,
                    ExitCode = processResult.ExitCode,
                    OutputLines = outputLines,
                    Error = "SteamCMD download was cancelled.",
                };
            }

            if (processResult.TimedOut)
            {
                return new DownloadResult
                {
                    WorkshopId = request.WorkshopId,
                    Status = OperationStatus.Failed,
                    ContentPath = contentPath,
                    FolderExists = folderExists,
                    FolderNonEmpty = folderNonEmpty,
                    ExitCode = processResult.ExitCode,
                    TimedOut = true,
                    OutputLines = outputLines,
                    Error = $"SteamCMD timed out after {request.Timeout}.",
                };
            }

            var terminalState = SteamCmdOutputClassifier.Classify(
                request.WorkshopId,
                processResult.Output);
            var succeeded = terminalState == SteamCmdTerminalState.Succeeded
                && folderExists
                && folderNonEmpty;
            progress?.Report(new OperationProgress(
                "DownloadingWorkshopItem",
                Completed: 1,
                Total: 1,
                request.WorkshopId,
                succeeded
                    ? $"Downloaded Workshop item {request.WorkshopId}."
                    : $"SteamCMD could not verify Workshop item {request.WorkshopId}."));

            return new DownloadResult
            {
                WorkshopId = request.WorkshopId,
                Status = succeeded ? OperationStatus.Succeeded : OperationStatus.Failed,
                ContentPath = contentPath,
                FolderExists = folderExists,
                FolderNonEmpty = folderNonEmpty,
                ExitCode = processResult.ExitCode,
                OutputLines = outputLines,
                Error = succeeded
                    ? null
                    : BuildDownloadError(terminalState, folderExists, folderNonEmpty, outputLines),
            };
        }
        catch (OperationCanceledException)
        {
            return new DownloadResult
            {
                WorkshopId = request.WorkshopId,
                Status = OperationStatus.Cancelled,
                ContentPath = contentPath,
                Error = "SteamCMD download was cancelled.",
            };
        }
        catch (Exception exception) when (IsOperationalFailure(exception))
        {
            return FailedDownload(request.WorkshopId, contentPath, exception.Message);
        }
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
                    cancellationToken).ConfigureAwait(false);
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
