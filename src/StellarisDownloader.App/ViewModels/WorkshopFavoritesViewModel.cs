using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using StellarisDownloader.Core.Models;
using StellarisDownloader.Core.Persistence;

namespace StellarisDownloader.App.ViewModels;

public sealed class WorkshopFavoritesViewModel(WorkshopFavoritesStore store) : ObservableObject
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

    public Task SetFavoriteAsync(string id, bool favorite, string? title = null) =>
        RunAsync(() => store.SetFavoriteAsync(id, favorite, title));

    private Task RunAsync(Func<Task<IReadOnlyList<WorkshopFavorite>>> operation)
    {
        var previous = pendingOperation;
        pendingOperation = RunAfterAsync(previous, operation);
        return pendingOperation;
    }

    private async Task RunAfterAsync(Task previous, Func<Task<IReadOnlyList<WorkshopFavorite>>> operation)
    {
        pendingOperations++;
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(IsIdle));
        try
        {
            await previous;
            LastError = null;
            Items = await operation();
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException
            or ArgumentException or InvalidOperationException or System.Security.SecurityException)
        {
            LastError = exception.Message;
        }
        finally
        {
            pendingOperations--;
            OnPropertyChanged(nameof(IsBusy));
            OnPropertyChanged(nameof(IsIdle));
        }
    }
}
