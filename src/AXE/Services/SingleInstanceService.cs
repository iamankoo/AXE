using System.Threading;

namespace AXE.Services;

/// <summary>
/// Keeps a single AXE instance per user session so the WebView2 profile is never shared by
/// two processes. A second launch (Start menu, Search, shortcut) asks the running instance
/// to restore itself and then exits.
/// </summary>
public sealed class SingleInstanceService : IDisposable
{
    private const string MutexName = @"Local\AXE.SingleInstance.5b0e7c9e";
    private const string ActivateEventName = @"Local\AXE.Activate.5b0e7c9e";

    private readonly Mutex _mutex;
    private readonly EventWaitHandle _activateEvent;
    private readonly bool _isPrimary;
    private RegisteredWaitHandle? _registration;

    public SingleInstanceService()
    {
        _mutex = new Mutex(initiallyOwned: true, MutexName, out _isPrimary);
        _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
    }

    public bool IsPrimary => _isPrimary;

    /// <summary>Called on the secondary instance: wakes the primary instance.</summary>
    public void SignalPrimary() => _activateEvent.Set();

    /// <summary>Called on the primary instance: invokes <paramref name="onActivate"/> on each later launch.</summary>
    public void ListenForActivation(Action onActivate)
    {
        _registration = ThreadPool.RegisterWaitForSingleObject(
            _activateEvent, (_, _) => onActivate(), null, Timeout.Infinite, executeOnlyOnce: false);
    }

    public void Dispose()
    {
        _registration?.Unregister(null);
        _activateEvent.Dispose();
        if (_isPrimary)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // not owned by this thread any more; process exit releases it
            }
        }

        _mutex.Dispose();
    }
}
