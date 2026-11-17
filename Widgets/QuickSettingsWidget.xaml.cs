using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using OpenTaskbar.Services;
using Windows.Devices.Radios;

namespace OpenTaskbar.Widgets;

/// <summary>
/// Wi-Fi and Bluetooth toggles plus shortcuts to Windows' own flyouts. These replace the tray area that disappears
/// with the native taskbar. When Windows denies radio access, the toggles fall back to opening the settings page.
/// </summary>
public partial class QuickSettingsWidget : DockWidget
{
    private Radio? _wifi;
    private Radio? _bluetooth;
    private bool _radioAccess;

    public QuickSettingsWidget()
    {
        InitializeComponent();
        Render();
    }

    protected override void OnLanguageChanged() => Render();

    protected override async void OnActivated()
    {
        try
        {
            _radioAccess = await Radio.RequestAccessAsync() == RadioAccessStatus.Allowed;
            if (!IsActive || !_radioAccess)
            {
                Render();
                return;
            }

            var radios = await Radio.GetRadiosAsync();
            if (!IsActive) return;
            _wifi = radios.FirstOrDefault(radio => radio.Kind == RadioKind.WiFi);
            _bluetooth = radios.FirstOrDefault(radio => radio.Kind == RadioKind.Bluetooth);
            if (_wifi is not null) _wifi.StateChanged += Radio_StateChanged;
            if (_bluetooth is not null) _bluetooth.StateChanged += Radio_StateChanged;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
            AppLog.Warning($"Radio access unavailable: {ex.Message}");
            _radioAccess = false;
        }

        Render();
    }

    protected override void OnDeactivated()
    {
        if (_wifi is not null) _wifi.StateChanged -= Radio_StateChanged;
        if (_bluetooth is not null) _bluetooth.StateChanged -= Radio_StateChanged;
        _wifi = null;
        _bluetooth = null;
    }

    private void Radio_StateChanged(Radio sender, object args) =>
        Dispatcher.BeginInvoke(DispatcherPriority.Background, Render);

    private void Render()
    {
        RenderRadio(_wifi, WifiPill, WifiToggle, WifiSettings, "WiFi");
        RenderRadio(_bluetooth, BluetoothPill, BluetoothToggle, BluetoothSettings, "Bluetooth");
    }

    private void RenderRadio(Radio? radio, Border pill, ToggleButton toggle, Button settings, string nameKey)
    {
        var name = UiText.Get(nameKey);
        // With access but no matching radio the device lacks that hardware; hide it rather than show a dead control.
        pill.Visibility = _radioAccess && radio is null && IsActive ? Visibility.Collapsed : Visibility.Visible;
        var on = radio?.State == RadioState.On;
        toggle.IsChecked = on;
        pill.SetResourceReference(Border.BackgroundProperty, on ? "SurfaceSelectedStrong" : "SurfaceSelected");
        pill.SetResourceReference(Border.BorderBrushProperty, on ? "WidgetOnStroke" : "StrokeSubtle");
        toggle.SetResourceReference(ForegroundProperty, on ? "WidgetAccent" : "TextPrimary");
        var toggleLabel = radio is null ? UiText.Format("RadioSettings", name) : UiText.Format("RadioToggle", name);
        toggle.ToolTip = toggleLabel;
        AutomationProperties.SetName(toggle, toggleLabel);
        var settingsLabel = UiText.Format("RadioSettings", name);
        settings.ToolTip = settingsLabel;
        AutomationProperties.SetName(settings, settingsLabel);
    }

    private async void ToggleRadio(Radio? radio, string settingsUri)
    {
        if (radio is null)
        {
            Report(ShellShortcuts.OpenUri(settingsUri));
            Render();
            return;
        }

        try
        {
            var target = radio.State == RadioState.On ? RadioState.Off : RadioState.On;
            var result = await radio.SetStateAsync(target);
            if (result != RadioAccessStatus.Allowed)
            {
                AppLog.Warning($"Radio {radio.Kind} state change denied: {result}");
                Report(ShellShortcuts.OpenUri(settingsUri));
            }
        }
        catch (System.Runtime.InteropServices.COMException ex)
        {
            AppLog.Warning($"Radio {radio.Kind} state change failed: {ex.Message}");
            Report(ShellShortcuts.OpenUri(settingsUri));
        }

        Render();
    }

    private void WifiToggle_Click(object sender, RoutedEventArgs e) => ToggleRadio(_wifi, "ms-settings:network-wifi");
    private void BluetoothToggle_Click(object sender, RoutedEventArgs e) => ToggleRadio(_bluetooth, "ms-settings:bluetooth");
    private void WifiSettings_Click(object sender, RoutedEventArgs e) => Report(ShellShortcuts.OpenUri("ms-settings:network-wifi"));
    private void BluetoothSettings_Click(object sender, RoutedEventArgs e) => Report(ShellShortcuts.OpenUri("ms-settings:bluetooth"));
    private void Display_Click(object sender, RoutedEventArgs e) => Report(ShellShortcuts.OpenProjection(sender as FrameworkElement ?? this));
    private void Hotspot_Click(object sender, RoutedEventArgs e) => Report(ShellShortcuts.OpenUri("ms-settings:network-mobilehotspot"));
    private void QuickSettings_Click(object sender, RoutedEventArgs e) => Report(ShellShortcuts.OpenQuickSettings(sender as FrameworkElement ?? this));
    private void Notifications_Click(object sender, RoutedEventArgs e) => Report(ShellShortcuts.OpenNotificationCenter());
}
