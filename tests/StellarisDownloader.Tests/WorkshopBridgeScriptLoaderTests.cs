using StellarisDownloader.App.Services;

namespace StellarisDownloader.Tests;

public sealed class WorkshopBridgeScriptLoaderTests
{
    [Fact]
    public void LoadReturnsIndependentRestrictedPostMessageResource()
    {
        var script = WorkshopBridgeScriptLoader.Load();

        Assert.Contains("enqueueWorkshopIds", script, StringComparison.Ordinal);
        Assert.Contains("window.chrome.webview.postMessage", script, StringComparison.Ordinal);
        Assert.Contains("MutationObserver", script, StringComparison.Ordinal);
        Assert.Contains("syncState", script, StringComparison.Ordinal);
        Assert.Contains("installedIds", script, StringComparison.Ordinal);
        Assert.DoesNotContain("executeCommand", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("localPath", script, StringComparison.OrdinalIgnoreCase);
    }
}
