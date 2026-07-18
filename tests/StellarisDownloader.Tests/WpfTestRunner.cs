using System.Collections.Concurrent;
using System.Windows.Threading;

namespace StellarisDownloader.Tests;

internal static class WpfTestRunner
{
    public static void Run(Func<Task> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        var exceptions = new ConcurrentQueue<Exception>();
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(
                new DispatcherSynchronizationContext(dispatcher));

            _ = ExecuteAsync();
            Dispatcher.Run();
            return;

            async Task ExecuteAsync()
            {
                try
                {
                    await action();
                }
                catch (Exception exception)
                {
                    exceptions.Enqueue(exception);
                }
                finally
                {
                    dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
                }
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (exceptions.TryDequeue(out var exception))
        {
            throw new InvalidOperationException("WPF dispatcher test action failed.", exception);
        }
    }
}
