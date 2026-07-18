using System.ComponentModel;
using System.Windows;
using StellarisDownloader.App.ViewModels;

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
}
