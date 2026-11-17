using System.Globalization;
using System.Windows;
using OpenTaskbar.Models;
using OpenTaskbar.Services;

namespace OpenTaskbar.Widgets;

public partial class SystemStatsWidget : DockWidget
{
    private const int LowBatteryPercent = 15;
    private const double TemperatureScaleCelsius = 100;
    private readonly SystemStats _stats = new();
    private SystemSensors? _sensors;
    private int _tick;

    public SystemStatsWidget()
    {
        InitializeComponent();
    }

    protected override void OnActivated()
    {
        _tick = 0;
        _stats.SampleCpu();
        _sensors = new SystemSensors(Settings.StatsMeters);
        ApplyMeterSelection();
    }

    protected override void OnDeactivated()
    {
        _sensors?.Dispose();
        _sensors = null;
    }

    protected override void OnSettingsChanged()
    {
        // Sample immediately so a re-enabled meter does not wait for the next 2 s cycle.
        _tick = 0;
        _sensors?.Configure(Settings.StatsMeters);
        ApplyMeterSelection();
    }

    protected override void OnTick(DateTime now)
    {
        // Readings every 2 s smooth CPU spikes; memory, battery and temperatures change slowly.
        if (_tick++ % 2 != 0) return;
        var meters = Settings.StatsMeters;

        if (meters.HasFlag(SystemMeters.Cpu) && _stats.SampleCpu() is { } cpu)
            SetUsage(CpuRing, CpuButton, cpu, "Cpu");
        if (meters.HasFlag(SystemMeters.Memory) && SystemStats.SampleMemory() is { } memory)
            SetUsage(MemoryRing, MemoryButton, memory, "Memory");
        UpdateBattery(meters.HasFlag(SystemMeters.Battery) ? SystemStats.SampleBattery() : new BatteryStatus(false, 0, false, false));
        if (_sensors is null) return;

        UpdateSensor(GpuButton, meters.HasFlag(SystemMeters.Gpu) ? _sensors.SampleGpuUsage() : null,
            usage => SetUsage(GpuRing, GpuButton, usage, "Gpu"));
        UpdateSensor(CpuTemperatureButton, meters.HasFlag(SystemMeters.CpuTemperature) ? _sensors.SampleCpuTemperature() : null,
            celsius => SetTemperature(CpuTemperatureRing, CpuTemperatureButton, celsius, "CpuTemperature"));
        UpdateSensor(GpuTemperatureButton, meters.HasFlag(SystemMeters.GpuTemperature) ? _sensors.SampleGpuTemperature() : null,
            celsius => SetTemperature(GpuTemperatureRing, GpuTemperatureButton, celsius, "GpuTemperature"));
    }

    // CPU and memory always have data, so their visibility follows the setting alone; the other meters
    // appear only while the machine actually reports a value.
    private void ApplyMeterSelection()
    {
        var meters = Settings.StatsMeters;
        CpuButton.Visibility = meters.HasFlag(SystemMeters.Cpu) ? Visibility.Visible : Visibility.Collapsed;
        MemoryButton.Visibility = meters.HasFlag(SystemMeters.Memory) ? Visibility.Visible : Visibility.Collapsed;
        if (!meters.HasFlag(SystemMeters.Battery)) BatteryButton.Visibility = Visibility.Collapsed;
        if (!meters.HasFlag(SystemMeters.Gpu)) GpuButton.Visibility = Visibility.Collapsed;
        if (!meters.HasFlag(SystemMeters.CpuTemperature)) CpuTemperatureButton.Visibility = Visibility.Collapsed;
        if (!meters.HasFlag(SystemMeters.GpuTemperature)) GpuTemperatureButton.Visibility = Visibility.Collapsed;
    }

    private static void UpdateSensor(FrameworkElement button, double? reading, Action<double> render)
    {
        button.Visibility = reading is null ? Visibility.Collapsed : Visibility.Visible;
        if (reading is { } value) render(value);
    }

    private static void SetUsage(RingMeter ring, FrameworkElement owner, double value, string labelKey)
    {
        var percent = (int)Math.Round(value * 100);
        SetRing(ring, value, percent, $"{UiText.Get(labelKey)}: {UiText.Format("Percent", percent)}", owner);
    }

    private static void SetTemperature(RingMeter ring, FrameworkElement owner, double celsius, string labelKey)
    {
        var rounded = (int)Math.Round(celsius);
        SetRing(ring, celsius / TemperatureScaleCelsius, rounded, $"{UiText.Get(labelKey)}: {rounded}°C", owner);
    }

    // Warning and critical colors start at 75 % and 90 % of the scale (75 °C and 90 °C for temperatures).
    private static void SetRing(RingMeter ring, double value, int shown, string tooltip, FrameworkElement owner)
    {
        ring.Value = value;
        ring.Text = shown.ToString(CultureInfo.InvariantCulture);
        var brushKey = value >= 0.9 ? "WidgetFocusAccent" : value >= 0.75 ? "WidgetWarning" : "WidgetAccent";
        if (!Equals(ring.Tag, brushKey))
        {
            ring.Tag = brushKey;
            ring.SetResourceReference(RingMeter.ValueBrushProperty, brushKey);
        }
        owner.ToolTip = tooltip;
    }

    private void UpdateBattery(BatteryStatus battery)
    {
        BatteryButton.Visibility = battery.Present ? Visibility.Visible : Visibility.Collapsed;
        if (!battery.Present) return;
        BatteryText.Text = battery.Percent.ToString(CultureInfo.InvariantCulture);
        ChargingIcon.Visibility = battery.Charging ? Visibility.Visible : Visibility.Collapsed;
        BatteryButton.SetResourceReference(BackgroundProperty,
            battery.Percent <= LowBatteryPercent && !battery.PluggedIn ? "WidgetFocusAccent" : "WidgetAccentFill");
        BatteryButton.ToolTip = $"{UiText.Get(battery.Charging ? "BatteryCharging" : "Battery")}: {UiText.Format("Percent", battery.Percent)}";
    }

    private void BatteryButton_Click(object sender, RoutedEventArgs e) => Report(ShellShortcuts.OpenUri("ms-settings:batterysaver"));

    private void TaskManager_Click(object sender, RoutedEventArgs e) => Report(ShellShortcuts.OpenTaskManager());
}
