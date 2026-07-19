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
        Assert.Contains("findPreviewTarget", script, StringComparison.Ordinal);
        Assert.Contains("link.querySelectorAll(\"img, video\")", script, StringComparison.Ordinal);
        Assert.Contains("attachShadow", script, StringComparison.Ordinal);
        Assert.Contains("recordsByTarget", script, StringComparison.Ordinal);
        Assert.Contains("previous.dispose", script, StringComparison.Ordinal);
        Assert.DoesNotContain("findCard", script, StringComparison.Ordinal);
        Assert.DoesNotContain("card.style.position", script, StringComparison.Ordinal);
        Assert.DoesNotContain("card.appendChild", script, StringComparison.Ordinal);
        Assert.DoesNotContain("executeCommand", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("localPath", script, StringComparison.OrdinalIgnoreCase);
    }
}
