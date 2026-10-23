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
using StellarisDownloader.Core.Services;

namespace StellarisDownloader.Tests;

public sealed class ModSnapshotsViewModelTests
{
    [Fact]
    public void SelectingAnOldListInTheWindowAutomaticallyComparesAgainstTheScannedLibrary()
    {
        WpfTestRunner.Run(async () =>
        {
            using var temporary = new TemporaryDirectory();
            var history = new ModSnapshotHistory(temporary.GetPath("snapshots"));
            var older = await history.SaveAsync(ModSnapshotTests.Snapshot("100"));
            await history.SaveAsync(ModSnapshotTests.Snapshot("200") with
            {
                CapturedAtUtc = DateTimeOffset.UtcNow,
            });
            var service = new SnapshotServiceStub(ModSnapshotTests.Snapshot("300"));
            var viewModel = new ModSnapshotsViewModel(service, history, key => key);
            await viewModel.InitializeAsync();
            Assert.Equal("200", Assert.Single(viewModel.Missing).WorkshopId);
            var window = new ModSnapshotsWindow(viewModel)
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -32000,
                Top = -32000,
                ShowInTaskbar = false,
            };
            try
            {
                window.Show();
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                viewModel.PropertyChanged += (_, args) =>
                {
                    if (args.PropertyName == nameof(viewModel.IsBusy) && !viewModel.IsBusy) { completed.TrySetResult(); }
                };
                var selector = Assert.IsType<ComboBox>(window.FindName("BaselineSelector"));
                selector.SelectedItem = viewModel.HistoryVersions.Single(item => item.FilePath == older);
                await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Null(viewModel.LastError);
                Assert.Equal("100", Assert.Single(viewModel.Missing).WorkshopId);
                Assert.Equal("300", Assert.Single(viewModel.Added).WorkshopId);
                Assert.Equal(1, service.CaptureCount);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public async Task CurrentComparisonAndReportPreserveDirectionAndMissingNames()
    {
        using var temporary = new TemporaryDirectory();
        var oldFile = temporary.GetPath("old.json");
        await ModSnapshotFile.WriteAsync(oldFile, ModSnapshotTests.Snapshot("100", "200"));
        var service = new SnapshotServiceStub(ModSnapshotTests.Snapshot("200", "300"));
        var viewModel = new ModSnapshotsViewModel(service, new ModSnapshotHistory(temporary.GetPath("history")), key => key);

        await viewModel.CompareCurrentAsync(oldFile);

        Assert.Null(viewModel.LastError);
        Assert.Equal("300", Assert.Single(viewModel.Added).WorkshopId);
        Assert.Equal("名称 100", Assert.Single(viewModel.Missing).DisplayTitle);
        Assert.Equal("200", Assert.Single(viewModel.Retained).WorkshopId);
        Assert.Equal("100", viewModel.GetMissingIds());
        Assert.True(viewModel.CanCopyMissing);
        Assert.True(viewModel.CanExportReport);
        Assert.Equal(1, service.CaptureCount);
        var reportPath = temporary.GetPath("report.txt");
        await viewModel.ExportReportAsync(reportPath);
        var report = await File.ReadAllTextAsync(reportPath);
        Assert.Contains("Snapshots.Missing (1)", report, StringComparison.Ordinal);
        Assert.Contains("名称 100 | 100 | https://steamcommunity.com/sharedfiles/filedetails/?id=100", report, StringComparison.Ordinal);
        Assert.Contains("Snapshots.Added (1)", report, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ComparingFilesDoesNotAccessTheCurrentLibraryAndBadFilesClearPreviousResults()
    {
        using var temporary = new TemporaryDirectory();
        var oldFile = temporary.GetPath("old.json");
        var newFile = temporary.GetPath("new.json");
        await ModSnapshotFile.WriteAsync(oldFile, ModSnapshotTests.Snapshot("100"));
        await ModSnapshotFile.WriteAsync(newFile, ModSnapshotTests.Snapshot("200"));
        var service = new SnapshotServiceStub(ModSnapshotTests.Snapshot());
        var viewModel = new ModSnapshotsViewModel(service, new ModSnapshotHistory(temporary.GetPath("history")), key => key);

        await viewModel.CompareFilesAsync(oldFile, newFile);
        Assert.NotNull(viewModel.Comparison);
        Assert.Equal(0, service.CaptureCount);
        await File.WriteAllTextAsync(newFile, "{}");
        await viewModel.CompareFilesAsync(oldFile, newFile);

        Assert.NotNull(viewModel.LastError);
        Assert.Null(viewModel.Comparison);
        Assert.Empty(viewModel.Missing);
        Assert.Empty(viewModel.BaselineItems);
        Assert.Empty(viewModel.CurrentItems);
        Assert.False(viewModel.CanExportReport);
        Assert.False(viewModel.CanCopyMissing);
        Assert.True(viewModel.IsIdle);
    }

    [Fact]
    public async Task ExportsJsonAndReadableTextWithUnnamedAndExcludedEntries()
    {
        using var temporary = new TemporaryDirectory();
        var snapshot = ModSnapshotTests.Snapshot("100", "200") with
        {
            Mods = [new ModSnapshotEntry { WorkshopId = "100", Title = "中文模组" },
                new ModSnapshotEntry { WorkshopId = "200" }],
            EmptyWorkshopIds = ["300"],
            LinkedWorkshopIds = ["400"],
        };
        var viewModel = new ModSnapshotsViewModel(new SnapshotServiceStub(snapshot), new ModSnapshotHistory(temporary.GetPath("history")), key => key);
        var jsonPath = temporary.GetPath("mods.json");
        var textPath = temporary.GetPath("mods.txt");

        await viewModel.ExportCurrentAsync(jsonPath, asText: false);
        await viewModel.ExportCurrentAsync(textPath, asText: true);

        Assert.Null(viewModel.LastError);
        Assert.Equal(2, (await ModSnapshotFile.ReadAsync(jsonPath)).Mods.Count);
        var text = await File.ReadAllTextAsync(textPath);
        Assert.Contains("中文模组 | 100", text, StringComparison.Ordinal);
        Assert.Contains("200 | 200", text, StringComparison.Ordinal);
        Assert.Contains("Snapshots.EmptyDirectories: 300", text, StringComparison.Ordinal);
        Assert.Contains("Snapshots.LinkedDirectories: 400", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BusyOperationsRejectReentryAndCancellationLeavesNoExport()
    {
        using var temporary = new TemporaryDirectory();
        var service = new SnapshotServiceStub(ModSnapshotTests.Snapshot("100"))
        {
            Pending = new TaskCompletionSource<ModSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        var viewModel = new ModSnapshotsViewModel(service, new ModSnapshotHistory(temporary.GetPath("history")), key => key);
        var path = temporary.GetPath("mods.json");
        var export = viewModel.ExportCurrentAsync(path, asText: false);
        Assert.True(viewModel.IsBusy);
        Assert.False(viewModel.IsIdle);
        await viewModel.ExportCurrentAsync(path, asText: false);
        Assert.Equal(1, service.CaptureCount);

        viewModel.Cancel();
        await export;

        Assert.False(File.Exists(path));
        Assert.True(viewModel.IsIdle);
        Assert.Null(viewModel.LastError);
        Assert.Equal("Snapshots.Cancelled", viewModel.StatusMessage);
    }

    [Theory]
    [InlineData("en")]
    [InlineData("zh-CN")]
    public void CompiledWindowRendersBothListsWithPinnedRedAndGreenRows(string language)
    {
        WpfTestRunner.Run(async () =>
        {
            using var temporary = new TemporaryDirectory();
            var resources = new ResourceDictionary();
            var baseline = ModSnapshotTests.Snapshot("100", "200", "900", "901") with
            {
                Mods = [Entry("100", "UI Overhaul Dynamic"), Entry("200", "星海舰船扩展"),
                    Entry("900", "Planetary Diversity"), Entry("901", "Ancient Cache of Technologies")],
            };
            var current = ModSnapshotTests.Snapshot("100", "200", "800", "801") with
            {
                Mods = [Entry("100", "UI Overhaul Dynamic · 4.0"), Entry("200", "星海舰船扩展"),
                    Entry("800", "More Events Mod"), Entry("801", "Gigastructural Engineering")],
                CapturedAtUtc = baseline.CapturedAtUtc.AddDays(1),
            };
            var history = new ModSnapshotHistory(temporary.GetPath("snapshots"));
            var baselinePath = await history.SaveAsync(baseline);
            var viewModel = new ModSnapshotsViewModel(
                new SnapshotServiceStub(current), history,
                key => (string)resources[key]);
            var window = new ModSnapshotsWindow(viewModel)
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -32000,
                Top = -32000,
                ShowInTaskbar = false,
            };
            resources.MergedDictionaries.Add(LocalizationService.LoadDictionary(language));
            window.Resources.MergedDictionaries.Add(resources);
            try
            {
                await viewModel.InitializeAsync();
                await viewModel.CompareCurrentAsync(baselinePath);
                window.Show();
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                window.UpdateLayout();
                Assert.Equal(resources["Snapshots.Title"], window.Title);
                Assert.Empty(Descendants(window).OfType<TabControl>());
                Assert.Single(Descendants(window).OfType<ComboBox>());
                var oldHost = Assert.IsType<ContentControl>(window.FindName("BaselineList"));
                var newHost = Assert.IsType<ContentControl>(window.FindName("CurrentList"));
                var oldGrid = Assert.Single(Descendants(oldHost).OfType<DataGrid>());
                var newGrid = Assert.Single(Descendants(newHost).OfType<DataGrid>());
                Assert.True(oldHost.TranslatePoint(new Point(), window).X < newHost.TranslatePoint(new Point(), window).X);
                foreach (var grid in new[] { oldGrid, newGrid })
                {
                    Assert.Equal(4, grid.Items.Count);
                    Assert.False(grid.CanUserSortColumns);
                    var first = Assert.IsType<ModSnapshotItemViewModel>(grid.Items[0]);
                    Assert.Equal(grid == oldGrid ? "901" : "801", first.WorkshopId);
                    Assert.Equal(grid == oldGrid, first.IsMissing);
                    Assert.Equal(grid == newGrid, first.IsAdded);
                    grid.SelectedIndex = 0;
                    window.UpdateLayout();
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    var row = Assert.IsType<DataGridRow>(grid.ItemContainerGenerator.ContainerFromIndex(0));
                    var expectedColor = (Color)ColorConverter.ConvertFromString(grid == oldGrid ? "#FDE7E9" : "#E6F4EA");
                    Assert.Equal(expectedColor, Assert.IsType<SolidColorBrush>(row.Background).Color);
                    Assert.Equal(3, grid.Columns.Count);
                    Assert.Equal(resources["Snapshots.ModName"], Assert.IsType<TextBlock>(grid.Columns[1].Header).Text);
                }

                Assert.Equal("UI Overhaul Dynamic", viewModel.BaselineItems.Single(item => item.WorkshopId == "100").DisplayTitle);
                Assert.Equal("UI Overhaul Dynamic · 4.0", viewModel.CurrentItems.Single(item => item.WorkshopId == "100").DisplayTitle);
                window.UpdateLayout();
                if (Environment.GetEnvironmentVariable("STELLARIS_SNAPSHOT_PREVIEW_DIR") is { Length: > 0 } previewDirectory)
                {
                    Directory.CreateDirectory(previewDirectory);
                    var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(window);
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var stream = File.Create(Path.Combine(previewDirectory, "mod-snapshots-" + language + ".png"));
                    encoder.Save(stream);
                }

                window.Width = window.MinWidth;
                window.Height = window.MinHeight;
                window.UpdateLayout();
                Assert.True(oldGrid.ActualHeight > 100);
                Assert.True(newGrid.ActualHeight > 100);
                Assert.True(Descendants(window).OfType<Button>().Single(button => button.IsCancel).IsEnabled);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public async Task SavedVersionsSurviveReopeningAndOpeningAutomaticallyComparesTheCurrentLibrary()
    {
        using var temporary = new TemporaryDirectory();
        var history = new ModSnapshotHistory(temporary.GetPath("snapshots"));
        var service = new SnapshotServiceStub(ModSnapshotTests.Snapshot("100"));
        var viewModel = new ModSnapshotsViewModel(service, history, key => key);
        await viewModel.InitializeAsync();
        Assert.Null(viewModel.SelectedBaselineVersion);
        Assert.Single(viewModel.CurrentItems);
        await viewModel.SaveVersionAsync();
        var first = Assert.Single(viewModel.HistoryVersions).FilePath!;
        var original = await File.ReadAllBytesAsync(first);
        await viewModel.SaveVersionAsync();

        Assert.Equal(2, viewModel.HistoryVersions.Count);
        Assert.Equal(first, viewModel.SelectedBaselineVersion?.FilePath);
        Assert.Equal(original, await File.ReadAllBytesAsync(first));
        var reopened = new ModSnapshotsViewModel(service, history, key => key);
        await reopened.InitializeAsync();
        Assert.NotNull(reopened.Comparison);
        await reopened.SelectBaselineAsync(reopened.HistoryVersions[1]);

        Assert.Equal(4, service.CaptureCount);
        Assert.Single(reopened.BaselineItems);
        Assert.Single(reopened.CurrentItems);
        Assert.Empty(reopened.Missing);
        Assert.Empty(reopened.Added);
        Assert.Equal(Path.Combine(history.DirectoryPath, "exports"), reopened.DefaultExportDirectory);
        await reopened.CompareFilesAsync(first, reopened.HistoryVersions[0].FilePath!);
        Assert.True(reopened.IsFileComparison);
        Assert.Equal(4, service.CaptureCount);
        await reopened.LoadHistoryAsync();
        Assert.False(reopened.IsFileComparison);
        Assert.Equal(5, service.CaptureCount);
    }

    [Fact]
    public async Task ExportCannotOverwriteAHistoryVersion()
    {
        using var temporary = new TemporaryDirectory();
        var history = new ModSnapshotHistory(temporary.GetPath("snapshots"));
        var originalPath = await history.SaveAsync(ModSnapshotTests.Snapshot("100"));
        var original = await File.ReadAllBytesAsync(originalPath);
        var service = new SnapshotServiceStub(ModSnapshotTests.Snapshot("200"));
        var viewModel = new ModSnapshotsViewModel(service, history, key => key);

        await viewModel.ExportCurrentAsync(originalPath, asText: false);

        Assert.NotNull(viewModel.LastError);
        Assert.Equal(0, service.CaptureCount);
        Assert.Equal(original, await File.ReadAllBytesAsync(originalPath));
    }

    private static ModSnapshotEntry Entry(string id, string title) => new() { WorkshopId = id, Title = title };

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            yield return child;
            foreach (var descendant in Descendants(child))
            {
                yield return descendant;
            }
        }
    }

    private sealed class SnapshotServiceStub(ModSnapshot snapshot) : IModSnapshotService
    {
        public int CaptureCount { get; private set; }

        public TaskCompletionSource<ModSnapshot>? Pending { get; init; }

        public Task<ModSnapshot> CaptureAsync(CancellationToken cancellationToken = default)
        {
            CaptureCount++;
            return Pending?.Task.WaitAsync(cancellationToken) ?? Task.FromResult(snapshot);
        }
    }
}
