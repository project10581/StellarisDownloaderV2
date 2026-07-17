using StellarisDownloader.Core.Services;

namespace StellarisDownloader.Tests;

public sealed class WriteOperationCoordinatorTests
{
    [Fact]
    public async Task WriteOperationsAreMutuallyExclusive()
    {
        using var coordinator = new WriteOperationCoordinator();
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var first = coordinator.ExecuteAsync(async cancellationToken =>
        {
            firstStarted.SetResult();
            await releaseFirst.Task.WaitAsync(cancellationToken);
            return 1;
        });
        await firstStarted.Task;

        var second = coordinator.ExecuteAsync(cancellationToken =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            secondStarted.SetResult();
            return Task.FromResult(2);
        });

        Assert.False(secondStarted.Task.IsCompleted);
        releaseFirst.SetResult();
        Assert.Equal(1, await first);
        Assert.Equal(2, await second);
        Assert.True(secondStarted.Task.IsCompleted);
    }
}
