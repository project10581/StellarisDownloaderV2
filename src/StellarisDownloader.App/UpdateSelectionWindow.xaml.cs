using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Documents;
using StellarisDownloader.App.ViewModels;
using StellarisDownloader.Core.Services;

namespace StellarisDownloader.App;

public partial class UpdateSelectionWindow : Window
{
    private readonly UpdateSelectionViewModel viewModel;

    public UpdateSelectionWindow(UpdateSelectionViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        InitializeComponent();
        this.viewModel = viewModel;
        DataContext = viewModel;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (viewModel.IsBusy)
        {
            viewModel.Cancel();
            e.Cancel = true;
            return;
        }

        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        viewModel.Dispose();
        base.OnClosed(e);
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (viewModel.Items.Count == 0 && !viewModel.IsBusy)
        {
            await viewModel.CheckUpdatesAsync().ConfigureAwait(true);
        }
    }

    private void OpenWorkshop_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Hyperlink { Tag: string workshopId }
            || !WorkshopIdParser.TryParse(workshopId, out var id, out _))
        {
            return;
        }

        e.Handled = true;
        try
        {
            Process.Start(new ProcessStartInfo($"https://steamcommunity.com/sharedfiles/filedetails/?id={id}")
            {
                UseShellExecute = true,
            });
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
            MessageBox.Show(this, exception.Message,
                TryFindResource("Open.ErrorTitle") as string ?? "Could not open item",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
