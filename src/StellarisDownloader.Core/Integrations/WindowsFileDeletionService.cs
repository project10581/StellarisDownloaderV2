using Microsoft.VisualBasic.FileIO;
using StellarisDownloader.Core.Services;

namespace StellarisDownloader.Core.Integrations;

public sealed class WindowsFileDeletionService : IFileDeletionService
{
    public Task SendDirectoryToRecycleBinAsync(
        string directoryPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateDirectory(directoryPath);
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Windows Recycle Bin deletion is available only on Windows.");
        }

        FileSystem.DeleteDirectory(
            directoryPath,
            UIOption.OnlyErrorDialogs,
            RecycleOption.SendToRecycleBin,
            UICancelOption.ThrowException);
        return Task.CompletedTask;
    }

    public Task DeleteDirectoryPermanentlyAsync(
        string directoryPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateDirectory(directoryPath);
        Directory.Delete(directoryPath, recursive: true);
        return Task.CompletedTask;
    }

    private static void ValidateDirectory(string directoryPath)
    {
        var attributes = File.GetAttributes(directoryPath);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException(
                $"Refusing to delete a reparse-point directory: {directoryPath}");
        }

        if ((attributes & FileAttributes.Directory) == 0)
        {
            throw new InvalidOperationException(
                $"Refusing to delete a path that is not a directory: {directoryPath}");
        }
    }
}
