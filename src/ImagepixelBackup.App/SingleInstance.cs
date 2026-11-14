using System;
using System.Threading;

namespace ImagepixelBackup.App;

/// <summary>
/// Makes sure the application runs only once per user session. A second start asks
/// the running instance to show its window and exits.
/// </summary>
internal sealed class SingleInstance : IDisposable
{
    /// <summary>The installer uses this name to notice that the application is running.</summary>
    public const string MutexName = "ImagepixelBackup.SingleInstance";

    private const string ShowEventName = "ImagepixelBackup.ShowWindow";

    private readonly Mutex _mutex;
    private readonly EventWaitHandle? _showEvent;
    private readonly ManualResetEvent _stop = new(false);

    private SingleInstance(Mutex mutex)
    {
        _mutex = mutex;
        if (OperatingSystem.IsWindows())
        {
            _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        }
    }

    /// <summary>Returns null if another instance is already running.</summary>
    public static SingleInstance? TryAcquire()
    {
        var mutex = new Mutex(false, MutexName, out var createdNew);
        if (createdNew)
        {
            return new SingleInstance(mutex);
        }

        mutex.Dispose();
        return null;
    }

    /// <summary>Asks the running instance to show its window.</summary>
    public static void SignalExisting()
    {
        if (OperatingSystem.IsWindows() && EventWaitHandle.TryOpenExisting(ShowEventName, out var showEvent))
        {
            using (showEvent)
            {
                showEvent.Set();
            }
        }
    }

    /// <summary>Calls the handler, on a background thread, whenever another start asks for the window.</summary>
    public void OnShowRequested(Action handler)
    {
        if (_showEvent is not { } showEvent)
        {
            return;
        }

        var thread = new Thread(() =>
        {
            while (WaitHandle.WaitAny([showEvent, _stop]) == 0)
            {
                handler();
            }
        })
        {
            IsBackground = true,
            Name = "SingleInstance",
        };
        thread.Start();
    }

    public void Dispose()
    {
        _stop.Set();
        _showEvent?.Dispose();
        _mutex.Dispose();
    }
}
