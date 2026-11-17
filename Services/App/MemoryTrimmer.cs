using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace OpenTaskbar.Services;

/// <summary>
/// Reclaims transient startup and popup allocations (Gen2 compacting GC + OS working-set trim)
/// once bursty UI work settles, keeping steady-state RAM minimal without polling loops.
/// </summary>
internal static class MemoryTrimmer
{
    private static readonly object Gate = new();
    private static DispatcherTimer? _timer;

    /// <summary>Coalesces trim requests so transient UI transitions settle before compacting.</summary>
    public static void ScheduleTrim(Dispatcher dispatcher, int delayMs = 1500)
    {
        if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished) return;

        void Arm()
        {
            lock (Gate)
            {
                if (_timer is null || !ReferenceEquals(_timer.Dispatcher, dispatcher))
                {
                    _timer?.Stop();
                    _timer = new DispatcherTimer(DispatcherPriority.ApplicationIdle, dispatcher);
                    _timer.Tick += OnTimerTick;
                }

                _timer.Stop();
                _timer.Interval = TimeSpan.FromMilliseconds(Math.Max(100, delayMs));
                _timer.Start();
            }
        }

        if (dispatcher.CheckAccess()) Arm();
        else dispatcher.BeginInvoke(DispatcherPriority.Background, (Action)Arm);
    }

    /// <summary>Performs an immediate compacting Gen2 collection and trims unused process working-set pages.</summary>
    public static void TrimNow()
    {
        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        _ = SetProcessWorkingSetSizeEx(GetCurrentProcess(), -1, -1, 0);
    }

    private static void OnTimerTick(object? sender, EventArgs e)
    {
        lock (Gate)
        {
            _timer?.Stop();
        }

        TrimNow();
    }

    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessWorkingSetSizeEx(nint process, nint minWorkingSetSize, nint maxWorkingSetSize, uint flags);
}
