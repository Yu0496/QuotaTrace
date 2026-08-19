namespace UsageTray.App;

public sealed class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;
    private bool _ownsMutex;

    private SingleInstance(Mutex mutex, bool ownsMutex)
    {
        _mutex = mutex;
        _ownsMutex = ownsMutex;
    }

    public static bool TryAcquire(string name, out SingleInstance? instance)
    {
        var mutex = new Mutex(true, name, out var createdNew);
        instance = new SingleInstance(mutex, createdNew);
        if (createdNew) return true;
        instance.Dispose();
        instance = null;
        return false;
    }

    public void Dispose()
    {
        if (!_ownsMutex) return;
        _ownsMutex = false;
        try { _mutex.ReleaseMutex(); } catch (ApplicationException) { }
        _mutex.Dispose();
    }
}
