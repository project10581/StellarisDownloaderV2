using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using StellarisDownloader.App.ViewModels;
using StellarisDownloader.Core.Services;

namespace StellarisDownloader.App;

public partial class MainWindow : Window
{
    private readonly MainWindowViewModel viewModel;
    private readonly DownloadQueueViewModel downloadQueueViewModel;
    private readonly ApplicationUpdateViewModel applicationUpdateViewModel;
    private readonly Func<Task<SettingsWindow>> settingsWindowFactory;
    private readonly Func<DownloadQueueWindow> downloadWindowFactory;
    private readonly Func<WorkshopBrowserWindow> browserWindowFactory;
    private readonly Func<UpdateSelectionWindow> updateWindowFactory;
    private readonly Func<ApplicationUpdateWindow> applicationUpdateWindowFactory;
    private DownloadQueueWindow? downloadWindow;
    private WorkshopBrowserWindow? browserWindow;

    public MainWindow(
        MainWindowViewModel viewModel,
        DownloadQueueViewModel downloadQueueViewModel,
        ApplicationUpdateViewModel applicationUpdateViewModel,
        Func<Task<SettingsWindow>> settingsWindowFactory,
        Func<DownloadQueueWindow> downloadWindowFactory,
        Func<WorkshopBrowserWindow> browserWindowFactory,
        Func<UpdateSelectionWindow> updateWindowFactory,
        Func<ApplicationUpdateWindow> applicationUpdateWindowFactory)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(downloadQueueViewModel);
        ArgumentNullException.ThrowIfNull(applicationUpdateViewModel);
        ArgumentNullException.ThrowIfNull(settingsWindowFactory);
        ArgumentNullException.ThrowIfNull(downloadWindowFactory);
        ArgumentNullException.ThrowIfNull(browserWindowFactory);
        ArgumentNullException.ThrowIfNull(updateWindowFactory);
        ArgumentNullException.ThrowIfNull(applicationUpdateWindowFactory);
        InitializeComponent();
        this.viewModel = viewModel;
        this.downloadQueueViewModel = downloadQueueViewModel;
        this.applicationUpdateViewModel = applicationUpdateViewModel;
        this.settingsWindowFactory = settingsWindowFactory;
        this.downloadWindowFactory = downloadWindowFactory;
        this.browserWindowFactory = browserWindowFactory;
        this.updateWindowFactory = updateWindowFactory;
        this.applicationUpdateWindowFactory = applicationUpdateWindowFactory;
        DataContext = viewModel;
    }

    private void DownloadModsButton_Click(object sender, RoutedEventArgs e)
    {
        ShowDownloadWindow();
    }

    private void ShowDownloadWindow()
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

    private async void DeleteSelected_Click(object sender, RoutedEventArgs e) =>
        await DeleteSelectedAsync(queueForRedownload: false).ConfigureAwait(true);

    private async void RedownloadSelected_Click(object sender, RoutedEventArgs e) =>
        await DeleteSelectedAsync(queueForRedownload: true).ConfigureAwait(true);

    private async Task DeleteSelectedAsync(bool queueForRedownload)
    {
        var selected = viewModel.SelectedMod;
        if (selected is null || !viewModel.CanModifySelectedMod)
        {
            return;
        }

        var message = FormatResource(
            queueForRedownload
                ? "Delete.RedownloadConfirmMessage"
                : "Delete.ConfirmMessage",
            selected.DisplayTitle,
            selected.WorkshopId);
        if (MessageBox.Show(
                this,
                message,
                TryFindResource("Delete.ConfirmTitle") as string ?? "Confirm mod deletion",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        var result = queueForRedownload
            ? await viewModel.DeleteAndQueueRedownloadAsync(permanently: false).ConfigureAwait(true)
            : await viewModel.DeleteSelectedAsync(permanently: false).ConfigureAwait(true);
        if (result.CanRetryPermanently)
        {
            var permanentMessage = FormatResource(
                "Delete.PermanentConfirmMessage",
                selected.DisplayTitle,
                selected.WorkshopId);
            if (MessageBox.Show(
                    this,
                    permanentMessage,
                    TryFindResource("Delete.ConfirmTitle") as string
                        ?? "Confirm permanent deletion",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Stop) == MessageBoxResult.Yes)
            {
                result = queueForRedownload
                    ? await viewModel.DeleteAndQueueRedownloadAsync(
                        permanently: true).ConfigureAwait(true)
                    : await viewModel.DeleteSelectedAsync(
                        permanently: true).ConfigureAwait(true);
            }
        }

        if (result.QueuedForRedownload)
        {
            ShowDownloadWindow();
        }

        if (!string.IsNullOrWhiteSpace(result.Error))
        {
            var error = result.RequiresRescan
                ? TryFindResource("Delete.RescanRequired") as string ?? result.Error
                : result.Error;
            MessageBox.Show(
                this,
                error,
                TryFindResource("Delete.ErrorTitle") as string ?? "Mod operation failed",
                MessageBoxButton.OK,
                result.RequiresRescan ? MessageBoxImage.Warning : MessageBoxImage.Error);
        }
    }

    private void OpenSelectedFolder_Click(object sender, RoutedEventArgs e)
    {
        var path = viewModel.SelectedMod?.Record.ContentPath;
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            ShowOpenError("The selected mod folder does not exist.");
            return;
        }

        OpenWithShell(path);
    }

    private void OpenSelectedWorkshop_Click(object sender, RoutedEventArgs e)
    {
        var target = viewModel.SelectedMod?.WorkshopUrl;
        if (!Uri.TryCreate(target, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !WorkshopIdParser.IsTrustedSteamCommunityHost(uri.IdnHost))
        {
            ShowOpenError("The selected Workshop link is invalid.");
            return;
        }

        OpenWithShell(uri.AbsoluteUri);
    }

    private async void RefreshMenuItem_Click(object sender, RoutedEventArgs e) =>
        await viewModel.RefreshAsync().ConfigureAwait(true);

    private async void RescanMenuItem_Click(object sender, RoutedEventArgs e) =>
        await viewModel.RescanAsync().ConfigureAwait(true);

    private void ModList_PreviewMouseRightButtonDown(
        object sender,
        MouseButtonEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(
                ModList,
                e.OriginalSource as DependencyObject) is ListBoxItem item)
        {
            item.IsSelected = true;
        }
    }

    private void OpenWithShell(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
            ShowOpenError(exception.Message);
        }
    }

    private void ShowOpenError(string message)
    {
        MessageBox.Show(
            this,
            message,
            TryFindResource("Open.ErrorTitle") as string ?? "Could not open item",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }

    private string FormatResource(string key, params object[] arguments)
    {
        var format = TryFindResource(key) as string ?? key;
        return string.Format(CultureInfo.CurrentCulture, format, arguments);
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

    private async void CheckUpdatesButton_Click(object sender, RoutedEventArgs e)
    {
        if (!viewModel.CanRunWriteOperations)
        {
            return;
        }

        var window = updateWindowFactory();
        window.Owner = this;
        window.ShowDialog();
        await viewModel.RefreshAsync().ConfigureAwait(true);
    }

    private void CheckAppUpdatesButton_Click(object sender, RoutedEventArgs e)
    {
        var window = applicationUpdateWindowFactory();
        window.Owner = this;
        window.ShowDialog();
    }

    public void PrepareForAppUpdateRestart()
    {
        if (viewModel.IsBusy || downloadQueueViewModel.IsBusy)
        {
            throw new InvalidOperationException(
                "Wait for the active mod operation before applying the application update.");
        }

        browserWindow?.Close();
        downloadWindow?.Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        viewModel.Dispose();
        base.OnClosed(e);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (applicationUpdateViewModel.IsBusy)
        {
            applicationUpdateViewModel.Cancel();
            e.Cancel = true;
            return;
        }

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
