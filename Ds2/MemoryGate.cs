namespace OdradekSharp.Ds2;

/// <summary>
/// Simple byte-budget gate: workers reserve the size of the data they are about to materialize and wait
/// until the budget allows it. Keeps the peak memory of a parallel export bounded regardless of the
/// thread count (a group whose spans exceed the whole budget is allowed to run alone).
/// </summary>
public sealed class MemoryGate(long budgetBytes) : IDisposable
{
    private readonly object _lock = new();
    private long _used;

    public long Budget => budgetBytes;
    public long Used { get { lock (_lock) return _used; } }

    public void Enter(long bytes)
    {
        lock (_lock)
        {
            while (_used > 0 && _used + bytes > budgetBytes)
                Monitor.Wait(_lock);
            _used += bytes;
        }
    }

    public void Exit(long bytes)
    {
        lock (_lock)
        {
            _used -= bytes;
            Monitor.PulseAll(_lock);
        }
    }

    public void Dispose()
    {
        lock (_lock) Monitor.PulseAll(_lock);
    }
}
