using System.Windows;
using StellarisDownloader.App.ViewModels;

namespace StellarisDownloader.App;

public partial class MainWindow : Window
{
    private readonly MainWindowViewModel viewModel;
    private readonly Func<Task<SettingsWindow>> settingsWindowFactory;

    public MainWindow(
        MainWindowViewModel viewModel,
        Func<Task<SettingsWindow>> settingsWindowFactory)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(settingsWindowFactory);
        InitializeComponent();
        this.viewModel = viewModel;
        this.settingsWindowFactory = settingsWindowFactory;
        DataContext = viewModel;
    }

    protected override void OnClosed(EventArgs e)
    {
        viewModel.Dispose();
        base.OnClosed(e);
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
