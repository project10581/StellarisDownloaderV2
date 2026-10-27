using System.Windows;

namespace StellarisDownloader.App.Services;

public interface IDialogService
{
    bool Confirm(string message, string title, MessageBoxImage image = MessageBoxImage.Question);

    void ShowError(string message, string title, MessageBoxImage image = MessageBoxImage.Error);
}
