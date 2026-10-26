using System.Collections.Concurrent;

namespace AmongLLM.GamePlugin;

// Script awaits resume through Update, while the game remains responsive.
internal sealed class UnityThreadContext(Func<bool> cancelled) : SynchronizationContext, IDisposable
{
    private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> queue = new();
    private bool disposed;

    public override void Post(SendOrPostCallback callback, object? state)
    {
        if (!disposed && !cancelled()) queue.Enqueue((callback, state));
    }

    public T Run<T>(Func<T> action)
    {
        SynchronizationContext? previous = Current;
        SetSynchronizationContext(this);
        try { return action(); }
        finally { SetSynchronizationContext(previous); }
    }

    public void Drain()
    {
        for (int i = 0; i < 32 && !disposed && !cancelled() && queue.TryDequeue(out var item); i++)
            Run(() => { item.Callback(item.State); return true; });
    }

    public void Dispose()
    {
        disposed = true;
        while (queue.TryDequeue(out _)) { }
    }
}
