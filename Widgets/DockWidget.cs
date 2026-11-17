using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using OpenTaskbar.Models;
using OpenTaskbar.Services;

namespace OpenTaskbar.Widgets;

/// <summary>
/// Base for dock widgets. The coordinator activates a widget while it is enabled in settings and ticks it once
/// per second; inactive widgets must not poll or hold system subscriptions.
/// </summary>
public class DockWidget : UserControl, IDisposable
{
    private bool _hasData = true;

    public DockWidget()
    {
        Visibility = Visibility.Collapsed;
        SetResourceReference(ForegroundProperty, "TextPrimary");
        Focusable = false;
        RenderTransform = new TransformGroup
        {
            Children = { new ScaleTransform(), new TranslateTransform() }
        };
        RenderTransformOrigin = new Point(0.5, 0.5);
    }

    public Action<string>? ReportError { get; set; }
    protected bool IsActive { get; private set; }
    protected DockSettings Settings { get; private set; } = new();

    /// <summary>False hides the widget while active, e.g. media controls with no playing session.</summary>
    protected bool HasData
    {
        get => _hasData;
        set
        {
            _hasData = value;
            UpdateVisibility();
        }
    }

    public void Activate()
    {
        if (IsActive) return;
        IsActive = true;
        UiText.Current.PropertyChanged += UiText_PropertyChanged;
        UpdateVisibility();
        OnActivated();
    }

    public void Deactivate()
    {
        if (!IsActive) return;
        IsActive = false;
        UiText.Current.PropertyChanged -= UiText_PropertyChanged;
        UpdateVisibility();
        OnDeactivated();
    }

    public void ApplySettings(DockSettings settings)
    {
        Settings = settings;
        OnSettingsChanged();
    }

    public void Tick(DateTime now)
    {
        if (IsActive) OnTick(now);
    }

    protected virtual void OnActivated() { }
    protected virtual void OnDeactivated() { }
    protected virtual void OnSettingsChanged() { }
    protected virtual void OnLanguageChanged() { }
    protected virtual void OnTick(DateTime now) { }

    private void UiText_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (IsActive) OnLanguageChanged();
    }

    protected void Report(string? error)
    {
        if (error is not null) ReportError?.Invoke(error);
    }

    private void UpdateVisibility() =>
        Visibility = IsActive && _hasData ? Visibility.Visible : Visibility.Collapsed;

    public void Dispose()
    {
        Deactivate();
        GC.SuppressFinalize(this);
    }
}
