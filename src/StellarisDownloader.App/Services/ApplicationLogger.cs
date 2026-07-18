using System.Globalization;
using System.IO;
using Serilog;

namespace StellarisDownloader.App.Services;

public static class ApplicationLogger
{
    public const long MaximumFileSizeBytes = 10L * 1024 * 1024;

    public static ILogger Create(string logsDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logsDirectory);
        var normalizedDirectory = Path.GetFullPath(logsDirectory);
        Directory.CreateDirectory(normalizedDirectory);

        return new LoggerConfiguration()
            .MinimumLevel.Information()
            .Enrich.FromLogContext()
            .WriteTo.File(
                Path.Combine(normalizedDirectory, "stellaris-downloader-.log"),
                rollingInterval: RollingInterval.Day,
                fileSizeLimitBytes: MaximumFileSizeBytes,
                rollOnFileSizeLimit: true,
                retainedFileCountLimit: null,
                retainedFileTimeLimit: TimeSpan.FromDays(14),
                shared: false,
                formatProvider: CultureInfo.InvariantCulture,
                outputTemplate:
                    "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}"
                    + "{NewLine}{Exception}")
            .CreateLogger();
    }
}
