using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;
using OpenTaskbar.Models;

namespace OpenTaskbar.Services;

public sealed class NativeWindowTracker : IDisposable
{
    private const uint EventObjectCreate = 0x8000;
    private const uint EventObjectDestroy = 0x8001;
    private const uint EventObjectShow = 0x8002;
    private const uint EventObjectHide = 0x8003;
    private const uint EventObjectNameChange = 0x800C;
    private const uint EventObjectLocationChange = 0x800B;
    private const uint EventSystemAlert = 0x0002;
    private const uint EventSystemForeground = 0x0003;
    private const uint WineventOutOfContext = 0x0000;
    private const int ObjectWindow = 0;
    private const int ChildIdSelf = 0;
    private const int WindowOwner = 4;
    private const int GwlExStyle = -20;
    private const long WsExAppWindow = 0x00040000;
    private const long WsExToolWindow = 0x00000080;
    private const uint GaRootOwner = 3;
    private const uint WmClose = 0x0010;
    private const int ShowRestore = 9;
    private const int ShowMinimize = 6;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const int ErrorInsufficientBuffer = 122;
    private static readonly string WindowsSystemAppsDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Windows), "SystemApps") + Path.DirectorySeparatorChar;

    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _refreshTimer;
    private readonly WinEventDelegate _eventCallback;
    private readonly EnumWindowsDelegate _enumCallback;
    private readonly HashSet<nint> _notificationWindows = [];
    private readonly List<TrackedWindow> _scratchWindows = [];
    private readonly Dictionary<nint, TrackedWindow> _previousWindows = [];
    private readonly StringBuilder _titleBuffer = new(256);
    private readonly StringBuilder _classBuffer = new(256);
    private readonly StringBuilder _pathBuffer = new(1024);
    private nint _windowHook;
    private nint _nameChangeHook;
    private nint _systemEventHook;
    private nint _excludedWindow;
    private nint _lastForegroundWindow;
    private nint _lastLocationForeground;
    private long _lastLocationTick;
    private int _refreshQueued;
    private int _urgentRefresh;
    private bool _disposed;

    public NativeWindowTracker(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _refreshTimer = new DispatcherTimer(DispatcherPriority.Input, dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(60)
        };
        _refreshTimer.Tick += RefreshTimer_Tick;
        _eventCallback = OnWinEvent;
        _enumCallback = CollectWindow;
    }

    public event EventHandler<IReadOnlyList<TrackedWindow>>? WindowsChanged;

    public event Action<nint>? ForegroundWindowChanged;

    public IReadOnlyList<TrackedWindow> Snapshot { get; private set; } = [];

    public bool NotificationsAvailable => _systemEventHook != 0;

    public void Start(nint excludedWindow)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_windowHook != 0) return;
        _excludedWindow = excludedWindow;
        _windowHook = SetWinEventHook(EventObjectCreate, EventObjectHide, 0, _eventCallback, 0, 0, WineventOutOfContext);
        if (_windowHook == 0)
        {
            throw new InvalidOperationException("Windows pencere bildirimleri dinlenemedi.");
        }

        _nameChangeHook = SetWinEventHook(EventObjectLocationChange, EventObjectNameChange, 0, _eventCallback, 0, 0, WineventOutOfContext);
        if (_nameChangeHook == 0)
        {
            UnhookWinEvent(_windowHook);
            _windowHook = 0;
            throw new InvalidOperationException("Pencere başlığı değişiklikleri dinlenemedi.");
        }

        _systemEventHook = SetWinEventHook(EventSystemAlert, EventSystemForeground, 0, _eventCallback, 0, 0, WineventOutOfContext);
        RefreshWindows();
        var foregroundWindow = GetForegroundWindow();
        if (foregroundWindow != 0)
        {
            ForegroundWindowChanged?.Invoke(foregroundWindow);
        }
    }

    private void OnWinEvent(nint hook, uint eventType, nint windowHandle, int objectId, int childId, uint eventThread, uint eventTime)
    {
        if (_disposed)
        {
            return;
        }

        if (eventType == EventObjectLocationChange)
        {
            if (objectId == ObjectWindow && childId == ChildIdSelf)
            {
                var foreground = GetForegroundWindow();
                if (foreground != 0 && (windowHandle == foreground || GetAncestor(windowHandle, GaRootOwner) == foreground))
                {
                    var now = Environment.TickCount64;
                    if (foreground == _lastLocationForeground && now - _lastLocationTick < 100)
                    {
                        return;
                    }

                    _lastLocationForeground = foreground;
                    _lastLocationTick = now;
                    QueueRefresh();
                    ForegroundWindowChanged?.Invoke(foreground);
                }
            }
            return;
        }

        if (eventType == EventSystemAlert && windowHandle != 0)
        {
            _notificationWindows.Add(windowHandle);
            QueueRefresh();
            return;
        }

        if (eventType == EventSystemForeground && windowHandle != 0)
        {
            _notificationWindows.Remove(windowHandle);
            _lastForegroundWindow = windowHandle;
            QueueRefresh(urgent: true);
            ForegroundWindowChanged?.Invoke(windowHandle);
            return;
        }

        if (objectId != ObjectWindow || childId != ChildIdSelf
            || eventType is not (EventObjectCreate or EventObjectDestroy or EventObjectShow or EventObjectHide or EventObjectNameChange))
        {
            return;
        }

        QueueRefresh(urgent: eventType is EventObjectShow or EventObjectDestroy
            || (eventType == EventObjectNameChange && !Snapshot.Any(window => window.Handle == windowHandle)));
    }

    private void QueueRefresh(bool urgent = false)
    {
        if (urgent) Interlocked.Exchange(ref _urgentRefresh, 1);
        if (Interlocked.Exchange(ref _refreshQueued, 1) != 0)
        {
            return;
        }

        _dispatcher.InvokeAsync(() =>
        {
            Interlocked.Exchange(ref _refreshQueued, 0);
            if (!_disposed)
            {
                if (Interlocked.Exchange(ref _urgentRefresh, 0) != 0)
                {
                    FlushRefresh();
                }
                else if (!_refreshTimer.IsEnabled)
                {
                    // Bound the wait from the first event; later events must not postpone discovery.
                    _refreshTimer.Start();
                }
            }
        }, DispatcherPriority.Input);
    }

    private void RefreshTimer_Tick(object? sender, EventArgs e)
    {
        FlushRefresh();
    }

    private void FlushRefresh()
    {
        _refreshTimer.Stop();
        RefreshWindows();
        var currentForeground = GetForegroundWindow();
        if (currentForeground != 0 && currentForeground != _lastForegroundWindow)
        {
            _lastForegroundWindow = currentForeground;
            ForegroundWindowChanged?.Invoke(currentForeground);
        }
    }

    private void RefreshWindows()
    {
        _scratchWindows.Clear();
        _previousWindows.Clear();
        foreach (var existing in Snapshot)
        {
            _previousWindows[existing.Handle] = existing;
        }

        EnumWindows(_enumCallback, 0);

        _notificationWindows.RemoveWhere(windowHandle => !IsWindow(windowHandle));

        if (Snapshot.SequenceEqual(_scratchWindows))
        {
            _scratchWindows.Clear();
            _previousWindows.Clear();
            return;
        }

        var snapshot = _scratchWindows.ToArray();
        _scratchWindows.Clear();
        _previousWindows.Clear();
        Snapshot = snapshot;
        WindowsChanged?.Invoke(this, snapshot);
    }

    private bool CollectWindow(nint windowHandle, nint state)
    {
        if (windowHandle == _excludedWindow || !IsWindowVisible(windowHandle)
            || GetWindow(windowHandle, WindowOwner) != 0)
        {
            return true;
        }

        GetWindowThreadProcessId(windowHandle, out var processId);
        if (processId == Environment.ProcessId) return true;

        var extendedStyle = GetWindowLongPtr(windowHandle, GwlExStyle).ToInt64();
        if ((extendedStyle & WsExToolWindow) != 0 && (extendedStyle & WsExAppWindow) == 0)
        {
            return true;
        }

        var titleLength = GetWindowTextLength(windowHandle);
        if (titleLength <= 0)
        {
            return true;
        }

        var needed = Math.Min(titleLength + 1, 1024);
        if (_titleBuffer.Capacity < needed) _titleBuffer.EnsureCapacity(needed);
        _titleBuffer.Clear();
        GetWindowText(windowHandle, _titleBuffer, _titleBuffer.Capacity);
        var value = _titleBuffer.ToString().Trim();
        if (value.Length > 0)
        {
            _previousWindows.TryGetValue(windowHandle, out var previous);
            var executablePath = previous?.ExecutablePath ?? GetExecutablePath(windowHandle, _pathBuffer);
            if (IsWindowsSystemApp(executablePath))
            {
                return true;
            }

            if (IsExplorerShellWindow(executablePath, windowHandle, _classBuffer))
            {
                return true;
            }

            if (previous is not null)
            {
                _scratchWindows.Add(previous with
                {
                    Title = value,
                    HasNotification = _notificationWindows.Contains(windowHandle)
                });
            }
            else
            {
                _scratchWindows.Add(new TrackedWindow(windowHandle, value,
                    executablePath, _notificationWindows.Contains(windowHandle)));
            }
        }

        return true;
    }

    public static bool Activate(nint windowHandle)
    {
        if (!IsWindow(windowHandle))
        {
            return false;
        }

        if (IsIconic(windowHandle))
        {
            ShowWindow(windowHandle, ShowRestore);
        }

        var targetThread = GetWindowThreadProcessId(windowHandle, out _);
        var currentThread = GetCurrentThreadId();
        var attached = targetThread != 0 && targetThread != currentThread
            && AttachThreadInput(currentThread, targetThread, true);
        try
        {
            BringWindowToTop(windowHandle);
            return SetForegroundWindow(windowHandle);
        }
        finally
        {
            if (attached)
            {
                AttachThreadInput(currentThread, targetThread, false);
            }
        }
    }

    public static nint GetForegroundApplicationWindow()
    {
        var foreground = GetForegroundWindow();
        GetWindowThreadProcessId(foreground, out var processId);
        if (foreground == 0 || processId == Environment.ProcessId) return 0;
        var root = GetAncestor(foreground, GaRootOwner);
        return root != 0 ? root : foreground;
    }

    public static bool ToggleActivation(nint windowHandle, string executablePath, nint? foregroundAtPress = null)
    {
        if (!IsWindow(windowHandle))
        {
            return false;
        }

        var foregroundWindow = foregroundAtPress ?? GetForegroundApplicationWindow();
        if (foregroundWindow != 0)
        {
            var foregroundRoot = GetAncestor(foregroundWindow, GaRootOwner);
            if (foregroundRoot == 0)
            {
                foregroundRoot = foregroundWindow;
            }

            if (IsWindow(foregroundRoot) && !IsIconic(foregroundRoot)
                && (foregroundRoot == windowHandle
                    || string.Equals(GetExecutablePath(foregroundRoot), executablePath, StringComparison.OrdinalIgnoreCase)))
            {
                return ShowWindowAsync(foregroundRoot, ShowMinimize);
            }
        }

        return Activate(windowHandle);
    }

    public static bool RequestClose(nint windowHandle)
    {
        return IsWindow(windowHandle) && PostMessage(windowHandle, WmClose, 0, 0);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _refreshTimer.Stop();
        _refreshTimer.Tick -= RefreshTimer_Tick;
        if (_windowHook != 0)
        {
            UnhookWinEvent(_windowHook);
            _windowHook = 0;
        }

        if (_nameChangeHook != 0)
        {
            UnhookWinEvent(_nameChangeHook);
            _nameChangeHook = 0;
        }

        if (_systemEventHook != 0)
        {
            UnhookWinEvent(_systemEventHook);
            _systemEventHook = 0;
        }
    }

    private delegate void WinEventDelegate(nint hook, uint eventType, nint windowHandle, int objectId, int childId, uint eventThread, uint eventTime);
    private delegate bool EnumWindowsDelegate(nint windowHandle, nint state);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetWinEventHook(uint eventMin, uint eventMax, nint eventHookModule, WinEventDelegate callback, uint processId, uint threadId, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWinEvent(nint hook);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsDelegate callback, nint state);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint windowHandle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(nint windowHandle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(nint windowHandle);

    [DllImport("user32.dll")]
    private static extern nint GetWindow(nint windowHandle, int command);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(nint windowHandle, StringBuilder text, int maximumCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(nint windowHandle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint windowHandle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BringWindowToTop(nint windowHandle);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint windowHandle, out uint processId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    private static string? GetExecutablePath(nint windowHandle, StringBuilder? buffer = null)
    {
        var threadId = GetWindowThreadProcessId(windowHandle, out var processId);
        if (threadId == 0 || processId == 0)
        {
            return null;
        }

        var processHandle = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (processHandle == 0)
        {
            return null;
        }

        try
        {
            var path = buffer ?? new StringBuilder(1024);
            path.Clear();
            var length = (uint)path.Capacity;
            if (QueryFullProcessImageName(processHandle, 0, path, ref length))
            {
                return path.ToString();
            }

            if (Marshal.GetLastPInvokeError() == ErrorInsufficientBuffer)
            {
                path.EnsureCapacity(32768);
                length = (uint)path.Capacity;
                return QueryFullProcessImageName(processHandle, 0, path, ref length) ? path.ToString() : null;
            }

            return null;
        }
        finally
        {
            CloseHandle(processHandle);
        }
    }

    // Packaged processes such as WhatsApp.Root.exe report their AppUserModelID even when their windows do not.
    internal static string? GetPackagedAppId(nint windowHandle)
    {
        var threadId = GetWindowThreadProcessId(windowHandle, out var processId);
        if (threadId == 0 || processId == 0)
        {
            return null;
        }

        var processHandle = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (processHandle == 0)
        {
            return null;
        }

        try
        {
            var length = 0u;
            if (GetApplicationUserModelId(processHandle, ref length, null) != ErrorInsufficientBuffer || length == 0)
            {
                return null;
            }

            var appId = new StringBuilder((int)length);
            return GetApplicationUserModelId(processHandle, ref length, appId) == 0 ? appId.ToString() : null;
        }
        finally
        {
            CloseHandle(processHandle);
        }
    }

    private static bool IsWindowsSystemApp(string? executablePath)
    {
        return executablePath is not null
            && executablePath.StartsWith(WindowsSystemAppsDirectory, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsExplorerShellWindow(string? executablePath, nint windowHandle, StringBuilder className)
    {
        if (!string.Equals(Path.GetFileName(executablePath), "explorer.exe", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        className.Clear();
        var length = GetClassName(windowHandle, className, className.Capacity);
        return length <= 0 || className.ToString() is not ("CabinetWClass" or "ExploreWClass");
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetApplicationUserModelId(nint processHandle, ref uint length, StringBuilder? applicationUserModelId);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern nint GetWindowLongPtr(nint windowHandle, int index);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern nint GetAncestor(nint windowHandle, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(nint windowHandle, StringBuilder className, int maximumCount);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(nint windowHandle, uint message, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachThreadInput(uint threadIdAttach, uint threadIdAttachTo, [MarshalAs(UnmanagedType.Bool)] bool attach);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(nint processHandle, uint flags, StringBuilder executablePath, ref uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint windowHandle, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindowAsync(nint windowHandle, int command);
}
