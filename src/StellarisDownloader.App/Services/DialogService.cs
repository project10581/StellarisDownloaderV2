using System.Windows;

namespace StellarisDownloader.App.Services;

public sealed class DialogService : IDialogService
{
    public bool Confirm(string message, string title, MessageBoxImage image = MessageBoxImage.Question) =>
        Show(message, title, MessageBoxButton.YesNo, image) == MessageBoxResult.Yes;

    public void ShowError(string message, string title, MessageBoxImage image = MessageBoxImage.Error) =>
        Show(message, title, MessageBoxButton.OK, image);

    private static MessageBoxResult Show(string message, string title, MessageBoxButton buttons, MessageBoxImage image)
    {
        var owner = Application.Current?.Windows.OfType<Window>().FirstOrDefault(window => window.IsActive);
        return owner is null ? MessageBox.Show(message, title, buttons, image)
            : MessageBox.Show(owner, message, title, buttons, image);
    }
}
