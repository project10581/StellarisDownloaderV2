using Serilog;
using StellarisDownloader.App.Services;

namespace StellarisDownloader.Tests;

public sealed class ApplicationLoggerTests
{
    [Fact]
    public void CreateWritesToTheConfiguredDirectory()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var logsDirectory = temporaryDirectory.GetPath("logs");
        var logger = ApplicationLogger.Create(logsDirectory);
        try
        {
            logger.Information("Logger verification {Value}.", 42);
        }
        finally
        {
            (logger as IDisposable)?.Dispose();
        }

        var logFile = Assert.Single(Directory.GetFiles(
            logsDirectory,
            "stellaris-downloader-*.log",
            SearchOption.TopDirectoryOnly));
        var content = File.ReadAllText(logFile);
        Assert.Contains("Logger verification 42.", content, StringComparison.Ordinal);
        Assert.Equal(10L * 1024 * 1024, ApplicationLogger.MaximumFileSizeBytes);
    }
}
