using System.Runtime.InteropServices;

namespace OpenTaskbar.Services;

/// <summary>
/// Tracks the default playback device through Core Audio: friendly name, master volume, and mute.
/// Events are raised on Core Audio worker threads; marshal to the UI thread before touching WPF.
/// </summary>
public sealed class AudioEndpointMonitor : IDisposable
{
    private static readonly object SharedLock = new();
    private static AudioEndpointMonitor? _sharedInstance;
    private static int _sharedRefCount;

    private const int DataFlowRender = 0;
    private const int RoleMultimedia = 1;
    private const int ClsCtxAll = 0x17;
    private const int StgmRead = 0;
    private static readonly Guid AudioEndpointVolumeId = new("5CDF2C82-841E-4546-9722-0CF74078229A");
    private static readonly PropertyKey FriendlyNameKey = new(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 14);

    private readonly IMMDeviceEnumerator _enumerator;
    private readonly NotificationClient _notificationClient;
    private readonly VolumeCallback _volumeCallback;
    private IAudioEndpointVolume? _volume;
    private Guid _eventContext = Guid.NewGuid();
    private long _pendingDeviceGeneration;
    private long _boundDeviceGeneration;
    private bool _disposed;

    public event Action? VolumeChanged;
    public event Action? DefaultDeviceChanged;

    public string? DeviceName { get; private set; }
    public bool HasDevice => _volume is not null;

    public AudioEndpointMonitor()
    {
        _enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
        _notificationClient = new NotificationClient(this);
        _volumeCallback = new VolumeCallback(this);
        Marshal.ThrowExceptionForHR(_enumerator.RegisterEndpointNotificationCallback(_notificationClient));
        BindDefaultDevice();
    }

    public float Volume
    {
        get
        {
            if (_volume is null) return 0;
            return _volume.GetMasterVolumeLevelScalar(out var level) == 0 ? level : 0;
        }
    }

    public bool Muted
    {
        get
        {
            if (_volume is null) return false;
            return _volume.GetMute(out var muted) == 0 && muted;
        }
    }

    public bool SetVolume(float level)
    {
        if (_volume is null) return false;
        var result = _volume.SetMasterVolumeLevelScalar(Math.Clamp(level, 0f, 1f), ref _eventContext);
        if (result == 0 && Muted) _volume.SetMute(false, ref _eventContext);
        return result == 0;
    }

    public bool ToggleMute() => _volume is not null && _volume.SetMute(!Muted, ref _eventContext) == 0;

    /// <summary>Rebinds to the current default playback device. Call on the UI thread.</summary>
    public void BindDefaultDevice()
    {
        if (_disposed) return;
        ReleaseVolume();
        DeviceName = null;
        if (_enumerator.GetDefaultAudioEndpoint(DataFlowRender, RoleMultimedia, out var device) != 0 || device is null)
        {
            return;
        }

        try
        {
            DeviceName = ReadFriendlyName(device);
            var iid = AudioEndpointVolumeId;
            if (device.Activate(ref iid, ClsCtxAll, 0, out var activated) == 0 && activated is IAudioEndpointVolume volume)
            {
                _volume = volume;
                _volume.RegisterControlChangeNotify(_volumeCallback);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(device);
        }
    }

    private static string? ReadFriendlyName(IMMDevice device)
    {
        if (device.OpenPropertyStore(StgmRead, out var store) != 0 || store is null) return null;
        try
        {
            var key = FriendlyNameKey;
            if (store.GetValue(ref key, out var value) != 0) return null;
            try
            {
                const ushort vtLpwstr = 31;
                return value.Type == vtLpwstr && value.Pointer != 0 ? Marshal.PtrToStringUni(value.Pointer) : null;
            }
            finally
            {
                PropVariantClear(ref value);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(store);
        }
    }

    private void ReleaseVolume()
    {
        if (_volume is null) return;
        _volume.UnregisterControlChangeNotify(_volumeCallback);
        Marshal.ReleaseComObject(_volume);
        _volume = null;
    }

    private void RaiseVolumeChanged()
    {
        if (!_disposed) VolumeChanged?.Invoke();
    }

    private void RaiseDefaultDeviceChanged()
    {
        if (_disposed) return;
        Interlocked.Increment(ref _pendingDeviceGeneration);
        DefaultDeviceChanged?.Invoke();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ReleaseVolume();
        _enumerator.UnregisterEndpointNotificationCallback(_notificationClient);
        Marshal.ReleaseComObject(_enumerator);
    }

    /// <summary>
    /// Acquires a reference-counted handle to a process-wide <see cref="AudioEndpointMonitor"/>,
    /// avoiding duplicate Core Audio COM enumerators and callbacks across widgets and dock wheel volume.
    /// </summary>
    public static SharedHandle AcquireShared()
    {
        lock (SharedLock)
        {
            if (_sharedInstance is null || _sharedInstance._disposed)
            {
                _sharedInstance = new AudioEndpointMonitor();
                _sharedRefCount = 0;
            }

            _sharedRefCount++;
            return new SharedHandle(_sharedInstance);
        }
    }

    private static void ReleaseShared(AudioEndpointMonitor instance)
    {
        lock (SharedLock)
        {
            if (!ReferenceEquals(_sharedInstance, instance)) return;
            if (--_sharedRefCount > 0) return;
            _sharedInstance = null;
            _sharedRefCount = 0;
            instance.Dispose();
        }
    }

    public sealed class SharedHandle : IDisposable
    {
        private readonly AudioEndpointMonitor _owner;
        private bool _disposed;

        internal SharedHandle(AudioEndpointMonitor owner)
        {
            _owner = owner;
            _owner.VolumeChanged += OnVolumeChanged;
            _owner.DefaultDeviceChanged += OnDefaultDeviceChanged;
        }

        public event Action? VolumeChanged;
        public event Action? DefaultDeviceChanged;

        public string? DeviceName => _disposed ? null : _owner.DeviceName;
        public bool HasDevice => !_disposed && _owner.HasDevice;
        public float Volume => _disposed ? 0 : _owner.Volume;
        public bool Muted => !_disposed && _owner.Muted;

        public bool SetVolume(float level) => !_disposed && _owner.SetVolume(level);
        public bool ToggleMute() => !_disposed && _owner.ToggleMute();

        public void BindDefaultDevice()
        {
            if (_disposed) return;
            var pending = Interlocked.Read(ref _owner._pendingDeviceGeneration);
            if (_owner._boundDeviceGeneration == pending) return;
            _owner._boundDeviceGeneration = pending;
            _owner.BindDefaultDevice();
        }

        private void OnVolumeChanged() => VolumeChanged?.Invoke();
        private void OnDefaultDeviceChanged() => DefaultDeviceChanged?.Invoke();

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _owner.VolumeChanged -= OnVolumeChanged;
            _owner.DefaultDeviceChanged -= OnDefaultDeviceChanged;
            ReleaseShared(_owner);
        }
    }

    private sealed class VolumeCallback(AudioEndpointMonitor owner) : IAudioEndpointVolumeCallback
    {
        public int OnNotify(nint notificationData)
        {
            owner.RaiseVolumeChanged();
            return 0;
        }
    }

    private sealed class NotificationClient(AudioEndpointMonitor owner) : IMMNotificationClient
    {
        public int OnDeviceStateChanged(string deviceId, uint newState) => 0;
        public int OnDeviceAdded(string deviceId) => 0;
        public int OnDeviceRemoved(string deviceId) => 0;

        public int OnDefaultDeviceChanged(int flow, int role, string? defaultDeviceId)
        {
            if (flow == DataFlowRender && role == RoleMultimedia) owner.RaiseDefaultDeviceChanged();
            return 0;
        }

        public int OnPropertyValueChanged(string deviceId, PropertyKey key) => 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PropertyKey(Guid format, uint id)
    {
        public Guid Format = format;
        public uint Id = id;
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort Type;
        [FieldOffset(8)] public nint Pointer;
    }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumeratorComObject;

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, uint stateMask, out nint devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice? endpoint);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice? device);
        [PreserveSig] int RegisterEndpointNotificationCallback(IMMNotificationClient client);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IMMNotificationClient client);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, nint activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object? instance);
        [PreserveSig] int OpenPropertyStore(int access, out IPropertyStore? properties);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out uint state);
    }

    [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
        [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
        [PreserveSig] int Commit();
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(IAudioEndpointVolumeCallback notify);
        [PreserveSig] int UnregisterControlChangeNotify(IAudioEndpointVolumeCallback notify);
        [PreserveSig] int GetChannelCount(out uint count);
        [PreserveSig] int SetMasterVolumeLevel(float levelDb, ref Guid eventContext);
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid eventContext);
        [PreserveSig] int GetMasterVolumeLevel(out float levelDb);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        [PreserveSig] int SetChannelVolumeLevel(uint channel, float levelDb, ref Guid eventContext);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid eventContext);
        [PreserveSig] int GetChannelVolumeLevel(uint channel, out float levelDb);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid eventContext);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
        [PreserveSig] int GetVolumeStepInfo(out uint step, out uint stepCount);
        [PreserveSig] int VolumeStepUp(ref Guid eventContext);
        [PreserveSig] int VolumeStepDown(ref Guid eventContext);
        [PreserveSig] int QueryHardwareSupport(out uint hardwareSupportMask);
        [PreserveSig] int GetVolumeRange(out float minDb, out float maxDb, out float incrementDb);
    }

    [ComImport, Guid("657804FA-D6AD-4496-8A60-352752AF4F89"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolumeCallback
    {
        [PreserveSig] int OnNotify(nint notificationData);
    }

    [ComImport, Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMNotificationClient
    {
        [PreserveSig] int OnDeviceStateChanged([MarshalAs(UnmanagedType.LPWStr)] string deviceId, uint newState);
        [PreserveSig] int OnDeviceAdded([MarshalAs(UnmanagedType.LPWStr)] string deviceId);
        [PreserveSig] int OnDeviceRemoved([MarshalAs(UnmanagedType.LPWStr)] string deviceId);
        [PreserveSig] int OnDefaultDeviceChanged(int flow, int role, [MarshalAs(UnmanagedType.LPWStr)] string? defaultDeviceId);
        [PreserveSig] int OnPropertyValueChanged([MarshalAs(UnmanagedType.LPWStr)] string deviceId, PropertyKey key);
    }

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant value);
}
