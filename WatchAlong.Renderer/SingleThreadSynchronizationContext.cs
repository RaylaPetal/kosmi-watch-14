using System.Collections.Concurrent;

namespace WatchAlong.Renderer;

/// <summary>
/// CEF requires <c>Cef.Initialize</c> and <c>Cef.Shutdown</c> to run on the exact same OS
/// thread. A console app has no <see cref="SynchronizationContext"/> installed, so every
/// <c>await</c> in <see cref="RendererApp.RunAsync"/> resumes on whatever ThreadPool thread
/// happened to complete the antecedent task — which is why Initialize and Shutdown landed on
/// different threads and crashed. Installing this on the entry thread makes every awaited
/// continuation post back onto that one thread via <see cref="RunOnCurrentThread"/> instead.
/// </summary>
internal sealed class SingleThreadSynchronizationContext : SynchronizationContext
{
    private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = new();

    public override void Post(SendOrPostCallback d, object? state) => _queue.Add((d, state));

    public override void Send(SendOrPostCallback d, object? state) =>
        throw new NotSupportedException("Synchronous Send is not needed by this app and would deadlock the pump.");

    /// <summary>Pumps posted continuations until <see cref="Complete"/> is called. Call this from the entry thread.</summary>
    public void RunOnCurrentThread()
    {
        foreach (var (callback, state) in _queue.GetConsumingEnumerable())
            callback(state);
    }

    public void Complete() => _queue.CompleteAdding();
}
