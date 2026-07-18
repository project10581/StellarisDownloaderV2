using System.IO;
using System.Reflection;

namespace StellarisDownloader.App.Services;

public static class WorkshopBridgeScriptLoader
{
    private const string ResourceName =
        "StellarisDownloader.App.Resources.WorkshopBridge.js";

    public static string Load()
    {
        var assembly = typeof(WorkshopBridgeScriptLoader).Assembly;
        using var stream = assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException(
                $"Embedded Workshop bridge resource '{ResourceName}' was not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
