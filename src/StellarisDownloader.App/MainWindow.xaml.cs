using System.ComponentModel;
using System.Windows;
using StellarisDownloader.App.ViewModels;

namespace StellarisDownloader.App;

public partial class MainWindow : Window
{
    private readonly MainWindowViewModel viewModel;
    private readonly DownloadQueueViewModel downloadQueueViewModel;
    private readonly Func<Task<SettingsWindow>> settingsWindowFactory;
    private readonly Func<DownloadQueueWindow> downloadWindowFactory;
    private readonly Func<WorkshopBrowserWindow> browserWindowFactory;
    private DownloadQueueWindow? downloadWindow;
    private WorkshopBrowserWindow? browserWindow;

    public MainWindow(
        MainWindowViewModel viewModel,
        DownloadQueueViewModel downloadQueueViewModel,
        Func<Task<SettingsWindow>> settingsWindowFactory,
        Func<DownloadQueueWindow> downloadWindowFactory,
        Func<WorkshopBrowserWindow> browserWindowFactory)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(downloadQueueViewModel);
        ArgumentNullException.ThrowIfNull(settingsWindowFactory);
        ArgumentNullException.ThrowIfNull(downloadWindowFactory);
        ArgumentNullException.ThrowIfNull(browserWindowFactory);
        InitializeComponent();
        this.viewModel = viewModel;
        this.downloadQueueViewModel = downloadQueueViewModel;
        this.settingsWindowFactory = settingsWindowFactory;
        this.downloadWindowFactory = downloadWindowFactory;
        this.browserWindowFactory = browserWindowFactory;
        DataContext = viewModel;
    }

    private void DownloadModsButton_Click(object sender, RoutedEventArgs e)
    {
        if (downloadWindow is not null)
        {
            downloadWindow.Activate();
            return;
        }

        var window = downloadWindowFactory();
        window.Owner = this;
        window.Closed += (_, _) => downloadWindow = null;
        downloadWindow = window;
        window.Show();
    }

    private void BrowseWorkshopButton_Click(object sender, RoutedEventArgs e)
    {
        if (browserWindow is not null)
        {
            browserWindow.Activate();
            return;
        }

        var window = browserWindowFactory();
        window.Owner = this;
        window.Closed += (_, _) => browserWindow = null;
        browserWindow = window;
        window.Show();
    }

    protected override void OnClosed(EventArgs e)
    {
        viewModel.Dispose();
        base.OnClosed(e);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (viewModel.IsBusy)
        {
            e.Cancel = true;
            MessageBox.Show(
                this,
                TryFindResource("Close.WaitForOperation") as string
                    ?? "Wait for the current library operation to finish before closing.",
                TryFindResource("Close.BusyTitle") as string ?? "Operation in progress",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        if (downloadQueueViewModel.IsBusy)
        {
            e.Cancel = true;
            var cancel = MessageBox.Show(
                this,
                TryFindResource("Close.CancelDownload") as string
                    ?? "A download is running. Cancel it before closing?",
                TryFindResource("Close.BusyTitle") as string ?? "Operation in progress",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (cancel == MessageBoxResult.Yes)
            {
                downloadQueueViewModel.Cancel();
            }

            return;
        }

        base.OnClosing(e);
    }

    private async void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var settingsWindow = await settingsWindowFactory().ConfigureAwait(true);
            settingsWindow.Owner = this;
            if (settingsWindow.ShowDialog() is true)
            {
                await viewModel.RefreshAsync().ConfigureAwait(true);
            }
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                this,
                exception.Message,
                TryFindResource("Settings.ErrorTitle") as string ?? "Settings error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }
}
