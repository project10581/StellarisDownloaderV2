using System.Diagnostics;
using StellarisDownloader.Core.Models;
using StellarisDownloader.Core.Services;

namespace StellarisDownloader.Core.Integrations;

public sealed class WindowsJunctionManager : IJunctionManager
{
    public Task<JunctionState> InspectAsync(
        string junctionPath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalizedPath = NormalizePath(junctionPath);
        return Task.FromResult(Inspect(normalizedPath));
    }

    public async Task<JunctionUpdate> SetTargetAsync(
        string junctionPath,
        string targetPath,
        CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Directory junctions are supported only on Windows.");
        }

        var normalizedJunctionPath = NormalizePath(junctionPath);
        var normalizedTargetPath = NormalizePath(targetPath);
        if (!Directory.Exists(normalizedTargetPath))
        {
            throw new DirectoryNotFoundException(
                $"The junction target directory does not exist: {normalizedTargetPath}");
        }

        var previousState = Inspect(normalizedJunctionPath);
        if (previousState.Kind == JunctionStateKind.Junction
            && PathsEqual(previousState.TargetPath, normalizedTargetPath))
        {
            return new JunctionUpdate(previousState, normalizedTargetPath, Changed: false);
        }

        EnsureReplaceable(previousState);
        var update = new JunctionUpdate(previousState, normalizedTargetPath, Changed: true);

        try
        {
            RemoveReplaceableEntry(previousState);
            await CreateJunctionAsync(
                normalizedJunctionPath,
                normalizedTargetPath,
                cancellationToken).ConfigureAwait(false);
            VerifyTarget(normalizedJunctionPath, normalizedTargetPath);
            return update;
        }
        catch
        {
            await RestoreCoreAsync(update, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public Task RestoreAsync(
        JunctionUpdate update,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        return RestoreCoreAsync(update, cancellationToken);
    }

    private static async Task RestoreCoreAsync(
        JunctionUpdate update,
        CancellationToken cancellationToken)
    {
        if (!update.Changed)
        {
            return;
        }

        var currentState = Inspect(update.PreviousState.Path);
        EnsureReplaceable(currentState);
        RemoveReplaceableEntry(currentState);

        switch (update.PreviousState.Kind)
        {
            case JunctionStateKind.Missing:
                return;
            case JunctionStateKind.EmptyDirectory:
                Directory.CreateDirectory(update.PreviousState.Path);
                return;
            case JunctionStateKind.Junction:
                if (update.PreviousState.TargetPath is null)
                {
                    throw new InvalidDataException("The previous junction target could not be resolved.");
                }

                await CreateJunctionAsync(
                    update.PreviousState.Path,
                    update.PreviousState.TargetPath,
                    cancellationToken).ConfigureAwait(false);
                VerifyTarget(update.PreviousState.Path, update.PreviousState.TargetPath);
                return;
            default:
                throw new InvalidOperationException(
                    $"Cannot restore junction state {update.PreviousState.Kind}.");
        }
    }

    private static JunctionState Inspect(string path)
    {
        if (!TryGetAttributes(path, out var attributes))
        {
            return new JunctionState(JunctionStateKind.Missing, path, TargetPath: null);
        }

        if ((attributes & FileAttributes.ReparsePoint) != 0
            && (attributes & FileAttributes.Directory) != 0)
        {
            string? targetPath = null;
            try
            {
                targetPath = new DirectoryInfo(path).ResolveLinkTarget(returnFinalTarget: true)?.FullName;
                if (targetPath is not null)
                {
                    targetPath = NormalizePath(targetPath);
                }
            }
            catch (IOException)
            {
                // Preserve the reparse-point classification so callers never recurse into its target.
            }

            return new JunctionState(JunctionStateKind.Junction, path, targetPath);
        }

        if ((attributes & FileAttributes.Directory) == 0)
        {
            return new JunctionState(JunctionStateKind.FileOrOther, path, TargetPath: null);
        }

        var kind = Directory.EnumerateFileSystemEntries(path).Any()
            ? JunctionStateKind.OccupiedDirectory
            : JunctionStateKind.EmptyDirectory;
        return new JunctionState(kind, path, TargetPath: null);
    }

    private static void EnsureReplaceable(JunctionState state)
    {
        if (state.Kind is JunctionStateKind.OccupiedDirectory or JunctionStateKind.FileOrOther)
        {
            throw new IOException(
                $"The SteamCMD workshop path is occupied and cannot be replaced automatically: {state.Path}");
        }

        if (state.Kind == JunctionStateKind.Junction && state.TargetPath is null)
        {
            throw new IOException(
                $"The existing junction target could not be resolved safely: {state.Path}");
        }
    }

    private static void RemoveReplaceableEntry(JunctionState state)
    {
        switch (state.Kind)
        {
            case JunctionStateKind.Missing:
                return;
            case JunctionStateKind.EmptyDirectory:
                Directory.Delete(state.Path, recursive: false);
                return;
            case JunctionStateKind.Junction:
                DeleteJunctionOnly(state.Path);
                return;
            default:
                throw new IOException($"Refusing to remove non-junction path: {state.Path}");
        }
    }

    private static void DeleteJunctionOnly(string path)
    {
        var state = Inspect(path);
        if (state.Kind != JunctionStateKind.Junction)
        {
            throw new IOException($"Refusing to delete a path that is not a directory junction: {path}");
        }

        Directory.Delete(path, recursive: false);
        if (TryGetAttributes(path, out _))
        {
            throw new IOException($"The junction still exists after deletion: {path}");
        }
    }

    private static async Task CreateJunctionAsync(
        string junctionPath,
        string targetPath,
        CancellationToken cancellationToken)
    {
        var parent = Path.GetDirectoryName(junctionPath)
            ?? throw new InvalidOperationException("The junction parent directory could not be resolved.");
        Directory.CreateDirectory(parent);

        var startInfo = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("/d");
        startInfo.ArgumentList.Add("/c");
        startInfo.ArgumentList.Add("mklink");
        startInfo.ArgumentList.Add("/J");
        startInfo.ArgumentList.Add(junctionPath);
        startInfo.ArgumentList.Add(targetPath);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start cmd.exe to create a directory junction.");
        var standardOutputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardErrorTask = process.StandardError.ReadToEndAsync(cancellationToken);

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            throw;
        }

        var standardOutput = await standardOutputTask.ConfigureAwait(false);
        var standardError = await standardErrorTask.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            var detail = string.IsNullOrWhiteSpace(standardError) ? standardOutput : standardError;
            throw new IOException(
                $"Failed to create junction {junctionPath} -> {targetPath}. Exit code: {process.ExitCode}. {detail.Trim()}");
        }
    }

    private static void VerifyTarget(string junctionPath, string expectedTargetPath)
    {
        var state = Inspect(junctionPath);
        if (state.Kind != JunctionStateKind.Junction
            || !PathsEqual(state.TargetPath, expectedTargetPath))
        {
            throw new IOException(
                $"Junction verification failed: {junctionPath} -> {state.TargetPath ?? "<unresolved>"}");
        }
    }

    private static bool TryGetAttributes(string path, out FileAttributes attributes)
    {
        try
        {
            attributes = File.GetAttributes(path);
            return true;
        }
        catch (FileNotFoundException)
        {
            attributes = default;
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            attributes = default;
            return false;
        }
    }

    private static string NormalizePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    private static bool PathsEqual(string? left, string? right) =>
        left is not null
        && right is not null
        && string.Equals(
            NormalizePath(left),
            NormalizePath(right),
            StringComparison.OrdinalIgnoreCase);
}
