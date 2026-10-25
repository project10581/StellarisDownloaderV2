using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using StellarisDownloader.Core.Models;
using StellarisDownloader.Core.Services;

namespace StellarisDownloader.App.ViewModels;

public sealed class WorkshopFavoritesViewModel(IWorkshopFavoritesStore store) : ObservableObject
{
    private IReadOnlyList<WorkshopFavorite> items = [];
    private string? lastError;
    private int pendingOperations;
    private Task pendingOperation = Task.CompletedTask;

    public IReadOnlyList<WorkshopFavorite> Items { get => items; private set => SetProperty(ref items, value); }
    public string? LastError { get => lastError; private set => SetProperty(ref lastError, value); }
    public bool IsBusy => pendingOperations > 0;
    public bool IsIdle => !IsBusy;
    public bool Contains(string id) => Items.Any(item => item.WorkshopId == id);

    public Task LoadAsync() => RunAsync(() => store.LoadAsync());

    public Task<bool> SetFavoriteAsync(string id, bool favorite, string? title = null) =>
        RunAsync(() => store.SetFavoriteAsync(id, favorite, title));

    public async Task FlushAsync()
    {
        Task operation;
        do
        {
            operation = pendingOperation;
            await operation;
        }
        while (!ReferenceEquals(operation, pendingOperation));
    }

    private Task<bool> RunAsync(Func<Task<IReadOnlyList<WorkshopFavorite>>> operation)
    {
        var previous = pendingOperation;
        var next = RunAfterAsync(previous, operation);
        pendingOperation = next;
        return next;
    }

    private async Task<bool> RunAfterAsync(Task previous, Func<Task<IReadOnlyList<WorkshopFavorite>>> operation)
    {
        pendingOperations++;
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(IsIdle));
        try
        {
            await previous;
            LastError = null;
            Items = await operation();
            return true;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException
            or ArgumentException or InvalidOperationException or System.Security.SecurityException)
        {
            LastError = exception.Message;
            return false;
        }
        finally
        {
            pendingOperations--;
            OnPropertyChanged(nameof(IsBusy));
            OnPropertyChanged(nameof(IsIdle));
        }
    }
}
