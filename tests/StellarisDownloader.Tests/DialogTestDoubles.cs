using System.Windows;
using StellarisDownloader.App.Services;

namespace StellarisDownloader.Tests;

internal sealed class StubDialogService(params bool[] confirmations) : IDialogService
{
    private readonly Queue<bool> answers = new(confirmations);
    public int ConfirmationCount { get; private set; }
    public List<string> Errors { get; } = [];

    public bool Confirm(string message, string title, MessageBoxImage image = MessageBoxImage.Question)
    {
        ConfirmationCount++;
        return answers.Dequeue();
    }

    public void ShowError(string message, string title, MessageBoxImage image = MessageBoxImage.Error) => Errors.Add(message);
}
