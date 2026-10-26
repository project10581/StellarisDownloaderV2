using System.ComponentModel;

namespace StellarisDownloader.Core.Services;

public static class OperationalFailure
{
    public static bool IsRecoverable(Exception exception, CancellationToken cancellationToken = default) =>
        exception is IOException or UnauthorizedAccessException or HttpRequestException or Win32Exception
            or StellarisOperationException
        || IsTimeout(exception, cancellationToken);

    public static bool IsTimeout(Exception exception, CancellationToken cancellationToken = default) =>
        exception is TaskCanceledException { InnerException: TimeoutException }
        && !cancellationToken.IsCancellationRequested;
}
