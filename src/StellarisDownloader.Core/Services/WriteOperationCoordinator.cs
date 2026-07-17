namespace StellarisDownloader.Core.Services;

public sealed class WriteOperationCoordinator : IDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);

    public bool IsBusy => gate.CurrentCount == 0;

    public async Task<TResult> ExecuteAsync<TResult>(
        Func<CancellationToken, Task<TResult>> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await operation(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    public void Dispose()
    {
        gate.Dispose();
        GC.SuppressFinalize(this);
    }
}
