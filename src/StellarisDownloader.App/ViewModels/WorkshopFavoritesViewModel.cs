using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using StellarisDownloader.Core.Models;
using StellarisDownloader.Core.Services;

namespace StellarisDownloader.App.ViewModels;

public sealed partial class WorkshopFavoritesViewModel(IWorkshopFavoritesStore store) : ObservableObject
{
    private int pendingOperations;
    private Task pendingOperation = Task.CompletedTask;

    [ObservableProperty]
    public partial IReadOnlyList<WorkshopFavorite> Items { get; private set; } = [];

    [ObservableProperty]
    public partial string? LastError { get; private set; }

    [ObservableProperty]
    public partial string? Warning { get; private set; }

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
            Warning = store.Warning;
            return true;
        }
        catch (Exception exception) when (OperationalFailure.IsRecoverable(exception))
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
