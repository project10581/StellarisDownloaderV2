using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using StellarisDownloader.App;
using StellarisDownloader.App.Services;
using StellarisDownloader.App.ViewModels;
using StellarisDownloader.Core.Models;
using StellarisDownloader.Core.Persistence;
using StellarisDownloader.Core.Services;

namespace StellarisDownloader.Tests;

public sealed class WorkshopBrowserPersistenceTests
{
    [BrowserRuntimeFact]
    public void RealBrowserPreservesFavoritesAndOpensDoubleClickedMods()
    {
        WpfTestRunner.Run(async () =>
        {
            using var temporary = new TemporaryDirectory();
            var favoritesPath = temporary.GetPath("favorites.json");
            using var queue = new DownloadQueueViewModel(new StubSettingsStore(new AppSettings()),
                new EmptyWorkshopClient(), new CompiledXamlTests.UnexpectedModOperationService());
            for (var attempt = 0; attempt < 2; attempt++)
            {
                using var store = new WorkshopFavoritesStore(favoritesPath);
                var favorites = new WorkshopFavoritesViewModel(store);
                var browser = new WorkshopBrowserWindow(queue, new InstalledState(attempt == 1), favorites)
                {
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = -32000,
                    Top = -32000,
                    ShowInTaskbar = false,
                };
                browser.Resources.MergedDictionaries.Add(LocalizationService.LoadDictionary("en"));
                var webView = Assert.IsType<WebView2>(browser.FindName("WorkshopWebView"));
                webView.CreationProperties = new CoreWebView2CreationProperties
                {
                    UserDataFolder = temporary.GetPath("webview-" + attempt),
                };
                var processExited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var runtimeInitialized = false;
                webView.CoreWebView2InitializationCompleted += (_, args) =>
                {
                    Assert.True(args.IsSuccess);
                    runtimeInitialized = true;
                    var core = webView.CoreWebView2;
                    core.Environment.BrowserProcessExited += (_, _) => processExited.TrySetResult();
                    core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All,
                        CoreWebView2WebResourceRequestSourceKinds.All);
                    core.WebResourceRequested += (_, request) =>
                    {
                        const string html = """
                            <html><head><title>Test mod</title></head><body>
                            <div class="workshopItem"><a href="https://steamcommunity.com/sharedfiles/filedetails/?id=123">
                            <img alt="Test mod" style="width:240px;height:180px" src="data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' width='240' height='180'%3E%3C/svg%3E"></a>
                            <div class="workshopItemTitle">Test mod</div></div>
                            </body></html>
                            """;
                        request.Response = core.Environment.CreateWebResourceResponse(
                            new MemoryStream(Encoding.UTF8.GetBytes(html)), 200, "OK", "Content-Type: text/html");
                    };
                };
                try
                {
                    // A queue window can request a page before WebView2 has initialized.
                    browser.OpenWorkshop("456");
                    browser.Show();
                    await WaitUntilAsync(() => Task.FromResult(webView.CoreWebView2 is not null));
                    await WaitUntilAsync(() => Task.FromResult(webView.CoreWebView2.Source == WorkshopUrl("456")));
                    await WaitUntilAsync(async () => await webView.ExecuteScriptAsync(
                        "!!document.getElementById('stellaris-downloader-workshop-overlay')?.shadowRoot.querySelector('.stellaris-downloader-favorite-button')") == "true");
                    if (attempt == 0)
                    {
                        var immediateState = await webView.ExecuteScriptAsync("""
                            (() => {
                                const star = document.getElementById('stellaris-downloader-workshop-overlay').shadowRoot.querySelector('.stellaris-downloader-favorite-button');
                                star.click();
                                return [star.dataset.state, window.__stellarisDownloaderWorkshopBridge.snapshot().favoriteIds.includes('123')];
                            })()
                            """);
                        Assert.Equal("[\"saving\",false]", immediateState);
                        await WaitUntilAsync(() => Task.FromResult(!favorites.IsBusy && favorites.Contains("123")));
                        Assert.Null(favorites.LastError);
                        Assert.Equal("123", Assert.Single(await store.LoadAsync()).WorkshopId);
                        await WaitUntilAsync(async () => await webView.ExecuteScriptAsync(
                            "document.getElementById('stellaris-downloader-workshop-overlay').shadowRoot.querySelector('.stellaris-downloader-favorite-button').dataset.state === 'favorited'") == "true");
                    }
                    else
                    {
                        Assert.Equal("123", Assert.Single(favorites.Items).WorkshopId);
                        await WaitUntilAsync(async () => await webView.ExecuteScriptAsync(
                            "window.__stellarisDownloaderWorkshopBridge.snapshot().favoriteIds.includes('123')") == "true");
                    }
                    Assert.Empty(queue.Items);
                    var sidebar = Assert.IsType<TabControl>(browser.FindName("BrowserSidebar"));
                    sidebar.SelectedIndex = 1;
                    await System.Windows.Threading.Dispatcher.Yield();
                    var favoriteList = Assert.IsType<ListBox>(browser.FindName("BrowserFavoritesList"));
                    Assert.Single(favoriteList.Items);
                    favoriteList.UpdateLayout();
                    Assert.True(DoubleClick(favoriteList.ItemContainerGenerator.ContainerFromIndex(0)).Handled);
                    await WaitUntilAsync(() => Task.FromResult(webView.CoreWebView2.Source == WorkshopUrl("123")));
                    Assert.Empty(queue.Items);
                    Assert.Single(favorites.Items);

                    if (attempt == 1)
                    {
                        await queue.EnqueueAsync("789\n101112");
                        sidebar.SelectedIndex = 0;
                        await System.Windows.Threading.Dispatcher.Yield();
                        var queueList = Assert.IsType<ListBox>(browser.FindName("BrowserQueueList"));
                        queueList.UpdateLayout();
                        queueList.SelectedIndex = 0;
                        var secondRow = queueList.ItemContainerGenerator.ContainerFromIndex(1);
                        Assert.False(DoubleClick(secondRow, MouseButton.Right).Handled);
                        Assert.False(DoubleClick(queueList).Handled);
                        Assert.Equal(WorkshopUrl("123"), webView.CoreWebView2.Source);
                        Assert.True(DoubleClick(secondRow).Handled);
                        await WaitUntilAsync(() => Task.FromResult(webView.CoreWebView2.Source == WorkshopUrl("101112")));

                        var queueWindow = new DownloadQueueWindow(queue)
                        {
                            WindowStartupLocation = WindowStartupLocation.Manual,
                            Left = -32000,
                            Top = -32000,
                            ShowInTaskbar = false,
                        };
                        queueWindow.WorkshopRequested += browser.OpenWorkshop;
                        try
                        {
                            queueWindow.Show();
                            queueWindow.UpdateLayout();
                            var separateList = Assert.IsType<ListBox>(queueWindow.FindName("QueueList"));
                            separateList.SelectedIndex = 1;
                            Assert.False(DoubleClick(separateList).Handled);
                            Assert.True(DoubleClick(separateList.ItemContainerGenerator.ContainerFromIndex(0)).Handled);
                            await WaitUntilAsync(() => Task.FromResult(webView.CoreWebView2.Source == WorkshopUrl("789")));
                            Assert.Equal(2, queue.Items.Count);
                            Assert.All(queue.Items, item => Assert.Equal(DownloadQueueItemStatus.Ready, item.Status));
                            Assert.Single(favorites.Items);
                        }
                        finally
                        {
                            queueWindow.Close();
                        }
                    }
                }
                finally
                {
                    browser.Close();
                    if (runtimeInitialized)
                    {
                        await processExited.Task.WaitAsync(TimeSpan.FromSeconds(10));
                    }
                }
            }
        });
    }

    private static string WorkshopUrl(string id) => $"https://steamcommunity.com/sharedfiles/filedetails/?id={id}";

    private static MouseButtonEventArgs DoubleClick(object target, MouseButton button = MouseButton.Left)
    {
        var args = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, button)
        {
            RoutedEvent = Control.MouseDoubleClickEvent,
        };
        Assert.IsAssignableFrom<Control>(target).RaiseEvent(args);
        return args;
    }

    private sealed class EmptyWorkshopClient : IWorkshopClient
    {
        public Task<IReadOnlyDictionary<string, WorkshopMetadata>> GetMetadataBatchAsync(
            IReadOnlyCollection<string> workshopIds, IProgress<OperationProgress>? progress = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<string, WorkshopMetadata>>(new Dictionary<string, WorkshopMetadata>());
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (!await condition()) { await Task.Delay(30, timeout.Token); }
    }

    private sealed class InstalledState(bool unavailable) : IInstalledWorkshopStateProvider
    {
        public Task<IReadOnlyList<string>> GetInstalledWorkshopIdsAsync(CancellationToken cancellationToken = default)
        {
            if (unavailable) { throw new IOException("Mod drive unavailable after restart."); }
            return Task.FromResult<IReadOnlyList<string>>([]);
        }
    }
}

public sealed class BrowserRuntimeFactAttribute : FactAttribute
{
    public BrowserRuntimeFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("STELLARIS_WEBVIEW_TEST") != "1")
        {
            Skip = "Set STELLARIS_WEBVIEW_TEST=1 to exercise the installed WebView2 runtime using an isolated local fixture.";
        }
    }
}
