using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using OpenTaskbar.Services;

namespace OpenTaskbar.Widgets;

public partial class AudioWidget : DockWidget
{
    private AudioEndpointMonitor.SharedHandle? _monitor;
    private bool _updating;

    public AudioWidget()
    {
        InitializeComponent();
    }

    protected override void OnLanguageChanged() => Render();

    protected override void OnActivated()
    {
        try
        {
            _monitor = AudioEndpointMonitor.AcquireShared();
            _monitor.VolumeChanged += Monitor_VolumeChanged;
            _monitor.DefaultDeviceChanged += Monitor_DefaultDeviceChanged;
        }
        catch (COMException ex)
        {
            AppLog.Warning($"Core Audio unavailable: {ex.Message}");
            _monitor = null;
        }

        Render();
    }

    protected override void OnDeactivated()
    {
        if (_monitor is null) return;
        _monitor.VolumeChanged -= Monitor_VolumeChanged;
        _monitor.DefaultDeviceChanged -= Monitor_DefaultDeviceChanged;
        _monitor.Dispose();
        _monitor = null;
    }

    // Both events arrive on Core Audio worker threads.
    private void Monitor_VolumeChanged() => Dispatcher.BeginInvoke(DispatcherPriority.Background, Render);

    private void Monitor_DefaultDeviceChanged() => Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
    {
        if (_monitor is null) return;
        _monitor.BindDefaultDevice();
        AppLog.Info($"Default audio device: {_monitor.DeviceName}");
        Render();
    });

    private void Render()
    {
        var hasDevice = _monitor?.HasDevice == true;
        MuteButton.IsEnabled = hasDevice;
        VolumeSlider.IsEnabled = hasDevice;
        if (!hasDevice)
        {
            DeviceText.Text = UiText.Get("NoAudioDevice");
            LevelText.Text = string.Empty;
            VolumeIcon.Content = FindResource("IconVolumeMute");
            return;
        }

        var level = (int)Math.Round(_monitor!.Volume * 100);
        var muted = _monitor.Muted;
        DeviceText.Text = _monitor.DeviceName ?? UiText.Get("Volume");
        Card.ToolTip = DeviceText.Text;
        LevelText.Text = muted ? "—" : UiText.Format("Percent", level);
        VolumeIcon.Content = FindResource(muted || level == 0 ? "IconVolumeMute" : level < 50 ? "IconVolumeLow" : "IconVolume");
        _updating = true;
        VolumeSlider.Value = level;
        _updating = false;
    }

    private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updating || _monitor is null) return;
        if (!_monitor.SetVolume((float)(e.NewValue / 100))) Report(UiText.Get("ErrorVolumeChange"));
    }

    private void MuteButton_Click(object sender, RoutedEventArgs e)
    {
        if (_monitor is not null && !_monitor.ToggleMute()) Report(UiText.Get("ErrorVolumeChange"));
    }

    private void Card_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_monitor?.HasDevice != true) return;
        VolumeSlider.Value = Math.Clamp(VolumeSlider.Value + Math.Sign(e.Delta) * 2, 0, 100);
        e.Handled = true;
    }
}
