using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using StellarisDownloader.App;
using StellarisDownloader.App.Services;
using StellarisDownloader.App.ViewModels;
using StellarisDownloader.Core.Models;
using StellarisDownloader.Core.Persistence;

namespace StellarisDownloader.Tests;

public sealed class WorkshopBrowserLayoutTests
{
    [Theory]
    [InlineData("en")]
    [InlineData("zh-CN")]
    public void SavedModsBelongToTheBrowserAndLayoutWorksAtMinimumSize(string language)
    {
        WpfTestRunner.Run(async () =>
        {
            using var temporary = new TemporaryDirectory();
            using var store = new WorkshopFavoritesStore(temporary.GetPath("favorites.json"));
            var favorites = new WorkshopFavoritesViewModel(store);
            await favorites.SetFavoriteAsync("100", true, "Gigastructural Engineering & More · 巨型结构扩展");
            await favorites.SetFavoriteAsync("200", true, "Planetary Diversity · 星球多样性");
            using var queue = new DownloadQueueViewModel(new StubSettingsStore(new AppSettings()),
                new CompiledXamlTests.UnexpectedWorkshopClient(), new CompiledXamlTests.UnexpectedModOperationService());
            var browser = new WorkshopBrowserWindow(queue, new EmptyInstalledState(), favorites);
            // Render the production layout without starting WebView2 or contacting Steam.
            var content = browser.Content;
            browser.Content = null;
            var host = new Window
            {
                Content = content,
                DataContext = queue,
                Width = 960,
                Height = 620,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -32000,
                Top = -32000,
                ShowInTaskbar = false,
            };
            host.Resources.MergedDictionaries.Add(LocalizationService.LoadDictionary(language));
            try
            {
                host.Show();
                var sidebar = Assert.IsType<TabControl>(browser.FindName("BrowserSidebar"));
                Assert.Equal(2, sidebar.Items.Count);
                sidebar.SelectedIndex = 1;
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                host.UpdateLayout();
                var list = Assert.IsType<ListBox>(browser.FindName("BrowserFavoritesList"));
                Assert.Equal(2, list.Items.Count);
                Assert.Empty(queue.Items);
                Assert.True(list.ActualHeight > 150);
                Assert.True(list.ActualWidth > 250);
                list.SelectedIndex = 0;
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                host.UpdateLayout();
                var actions = Descendants(Assert.IsType<Grid>(browser.FindName("FavoritesPanel"))).OfType<Button>().ToArray();
                Assert.Equal(3, actions.Length);
                Assert.All(actions, button =>
                {
                    Assert.True(button.IsEnabled);
                    var point = button.TranslatePoint(new Point(), host);
                    Assert.InRange(point.X + button.ActualWidth, 0, host.ActualWidth);
                    Assert.InRange(point.Y + button.ActualHeight, 0, host.ActualHeight);
                });
                var previewDirectory = Environment.GetEnvironmentVariable("STELLARIS_SNAPSHOT_PREVIEW_DIR");
                if (!string.IsNullOrEmpty(previewDirectory))
                {
                    Directory.CreateDirectory(previewDirectory);
                    var bitmap = new RenderTargetBitmap((int)host.ActualWidth, (int)host.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(host);
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var stream = File.Create(Path.Combine(previewDirectory, $"workshop-favorites-{language}.png"));
                    encoder.Save(stream);
                }
            }
            finally
            {
                host.Close();
                browser.Close();
            }
        });
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            yield return child;
            foreach (var nested in Descendants(child)) { yield return nested; }
        }
    }

    private sealed class EmptyInstalledState : IInstalledWorkshopStateProvider
    {
        public Task<IReadOnlyList<string>> GetInstalledWorkshopIdsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);
    }
}
