using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using OpenTaskbar.Services;

namespace OpenTaskbar.Views.Widgets;

public sealed class WidgetPanelHost : IDisposable
{
    private readonly Window _owner;
    private readonly WidgetPanelFrame _frame = new();
    private readonly Popup _popup;
    private FrameworkElement? _anchor;
    private bool _restoreFocus;
    private bool _disposed;

    public WidgetPanelHost(Window owner)
    {
        _owner = owner;
        _popup = new Popup
        {
            AllowsTransparency = true,
            StaysOpen = false,
            Placement = PlacementMode.Top,
            VerticalOffset = -(double)owner.FindResource("WidgetPanelGap"),
            PopupAnimation = PopupAnimation.None,
            Child = _frame
        };
        _popup.Opened += Popup_Opened;
        _popup.Closed += Popup_Closed;
        _frame.CloseRequested += Frame_CloseRequested;
        _frame.PreviewKeyDown += Frame_PreviewKeyDown;
        _owner.LocationChanged += Owner_LayoutChanged;
        _owner.SizeChanged += Owner_SizeChanged;
        _owner.Deactivated += Owner_Deactivated;
    }

    public bool IsOpen => _popup.IsOpen;
    public event EventHandler? IsOpenChanged;
    public event Action<string>? OperationFailed;

    public void Open(FrameworkElement anchor, string heading, FrameworkElement body)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsOpen && ReferenceEquals(_anchor, anchor))
        {
            Close();
            return;
        }
        Close(restoreFocus: false);
        if (!_owner.IsActive && !_owner.Activate())
        {
            OperationFailed?.Invoke(UiText.Get("WidgetPanelFocusError"));
            return;
        }
        _anchor = anchor;
        _restoreFocus = true;
        _frame.Heading = heading;
        _frame.Body = body;
        var gap = (double)_owner.FindResource("WidgetPanelGap");
        _frame.MaxWidth = Math.Max(1, SystemParameters.WorkArea.Width - gap * 2);
        _frame.MaxHeight = Math.Max(1, SystemParameters.WorkArea.Height - gap * 2);
        _popup.HorizontalOffset = (anchor.ActualWidth - Math.Min(_frame.Width, _frame.MaxWidth)) / 2;
        _popup.PlacementTarget = anchor;
        _popup.IsOpen = true;
    }

    public void Close(bool restoreFocus = true)
    {
        _restoreFocus = restoreFocus;
        _popup.IsOpen = false;
    }

    private void Popup_Opened(object? sender, EventArgs e)
    {
        IsOpenChanged?.Invoke(this, EventArgs.Empty);
        _frame.UpdateLayout();
        if (_popup.IsOpen && _frame.Body is { } body && !body.MoveFocus(new TraversalRequest(FocusNavigationDirection.First)))
            OperationFailed?.Invoke(UiText.Get("WidgetPanelFocusError"));
    }

    private void Popup_Closed(object? sender, EventArgs e)
    {
        var anchor = _anchor;
        _anchor = null;
        _frame.Body = null;
        _popup.PlacementTarget = null;
        if (_restoreFocus && _owner.IsActive && anchor is { IsLoaded: true, IsVisible: true })
            anchor.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
        IsOpenChanged?.Invoke(this, EventArgs.Empty);
        MemoryTrimmer.ScheduleTrim(_owner.Dispatcher, 1000);
    }

    private void Frame_CloseRequested(object? sender, EventArgs e) => Close();
    private void Frame_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        Close();
    }
    private void Owner_LayoutChanged(object? sender, EventArgs e) => Close(restoreFocus: false);
    private void Owner_SizeChanged(object sender, SizeChangedEventArgs e) => Close(restoreFocus: false);
    private void Owner_Deactivated(object? sender, EventArgs e) => Close(restoreFocus: false);

    public void Dispose()
    {
        if (_disposed) return;
        Close(restoreFocus: false);
        _popup.Opened -= Popup_Opened;
        _popup.Closed -= Popup_Closed;
        _frame.CloseRequested -= Frame_CloseRequested;
        _frame.PreviewKeyDown -= Frame_PreviewKeyDown;
        _owner.LocationChanged -= Owner_LayoutChanged;
        _owner.SizeChanged -= Owner_SizeChanged;
        _owner.Deactivated -= Owner_Deactivated;
        _frame.Body = null;
        _popup.Child = null;
        _disposed = true;
    }
}
