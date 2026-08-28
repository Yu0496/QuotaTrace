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
        try
        {
            var mutex = new Mutex(false, name);
            bool hasHandle = false;
            try
            {
                hasHandle = mutex.WaitOne(0, false);
            }
            catch (AbandonedMutexException)
            {
                hasHandle = true;
            }

            if (hasHandle)
            {
                instance = new SingleInstance(mutex, true);
                return true;
            }

            mutex.Dispose();
        }
        catch
        {
            // ignored
        }

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

