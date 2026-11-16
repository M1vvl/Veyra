namespace GlassDock.Services;

/// <summary>A timed-out native operation retains its one worker until it really exits.
/// A timeout never starts a replacement, queues retries, or cancels native COM by force.</summary>
internal sealed class SingleFlightOperation<T>
{
    private readonly object _sync = new();
    private Task<T>? _pending;
    internal Task<T> StartOrJoin(Func<T> action)
    {
        lock (_sync)
        {
            if (_pending is null || _pending.IsCompleted) _pending = Task.Run(action);
            return _pending;
        }
    }
}
