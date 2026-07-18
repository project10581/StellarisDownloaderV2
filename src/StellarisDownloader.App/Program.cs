using Serilog;
using StellarisDownloader.App.Services;
using StellarisDownloader.Core.Persistence;
using Velopack;

namespace StellarisDownloader.App;

public static class Program
{
    [STAThread]
    public static void Main()
    {
        VelopackApp.Build().Run();

        var paths = AppDataPaths.CreateDefault();
        paths.EnsureDirectories();
        Log.Logger = ApplicationLogger.Create(paths.LogsDirectory);
        try
        {
            Log.Information("Starting Stellaris Downloader V2.");
            var application = new App();
            application.InitializeComponent();
            application.Run();
        }
        catch (Exception exception)
        {
            Log.Fatal(exception, "Stellaris Downloader V2 terminated unexpectedly.");
            throw;
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }
}
