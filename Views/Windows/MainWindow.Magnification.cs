using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using OpenTaskbar.Services;

namespace OpenTaskbar;

public partial class MainWindow
{
    private readonly HashSet<FrameworkElement> _currentMagnifiedBuffer = [];

    internal static (ScaleTransform Scale, TranslateTransform Translate) GetIconTransforms(FrameworkElement icon)
    {
        if (icon.RenderTransform is TransformGroup { IsFrozen: false, IsSealed: false } group)
        {
            var s = group.Children.OfType<ScaleTransform>().FirstOrDefault();
            var t = group.Children.OfType<TranslateTransform>().FirstOrDefault();
            if (s is { IsFrozen: false, IsSealed: false } && t is { IsFrozen: false, IsSealed: false })
            {
                return (s, t);
            }
        }

        var newScale = new ScaleTransform();
        var newTranslate = new TranslateTransform();
        var newGroup = new TransformGroup();
        newGroup.Children.Add(newScale);
        newGroup.Children.Add(newTranslate);
        icon.RenderTransform = newGroup;
        icon.RenderTransformOrigin = new Point(0.5, 0.95);
        return (newScale, newTranslate);
    }

    private static FrameworkElement? GetButtonIcon(Button button) =>
        button.Content as FrameworkElement ?? (FrameworkElement?)FindVisualChild<Image>(button) ?? FindVisualChild<System.Windows.Shapes.Path>(button);

    private IEnumerable<Button> GetAllDockButtons()
    {
        yield return StartButton;

        // Profiles filter the pinned view, so look containers up by item rather than by index.
        foreach (var app in _pinnedApps)
        {
            if (PinnedApps.ItemContainerGenerator.ContainerFromItem(app) is DependencyObject container
                && FindVisualChild<Button>(container) is { } button)
            {
                yield return button;
            }
        }

        foreach (var app in _runningApps)
        {
            if (RunningApps.ItemContainerGenerator.ContainerFromItem(app) is DependencyObject container
                && FindVisualChild<Button>(container) is { } button)
            {
                yield return button;
            }
        }

        foreach (var button in GetExtraDockButtons()) yield return button;
    }

    private void AnimateActivationFeedback(Button button)
    {
        if (!_preferences.Current.AnimationsEnabled || !SystemParameters.ClientAreaAnimation)
        {
            return;
        }

        var icon = GetButtonIcon(button);
        if (icon is null) return;
        var (_, translate) = GetIconTransforms(icon);

        var bounce = new DoubleAnimationUsingKeyFrames
        {
            Duration = TimeSpan.FromMilliseconds(DockMetrics.LaunchBounceMs),
            KeyFrames =
            {
                new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)),
                new EasingDoubleKeyFrame(-DockMetrics.LaunchBounceHeight * BounceDirection, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(120)),
                    new CubicEase { EasingMode = EasingMode.EaseOut }),
                new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(240)),
                    new CubicEase { EasingMode = EasingMode.EaseIn }),
                new EasingDoubleKeyFrame(-5 * BounceDirection, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(330)),
                    new CubicEase { EasingMode = EasingMode.EaseOut }),
                new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(DockMetrics.LaunchBounceMs)),
                    new CubicEase { EasingMode = EasingMode.EaseIn })
            }
        };
        translate.BeginAnimation(IsVertical ? TranslateTransform.XProperty : TranslateTransform.YProperty, bounce);
    }

    private void DockSurface_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_preferences.Current.MagnificationEnabled || !_preferences.Current.AnimationsEnabled || !SystemParameters.ClientAreaAnimation)
        {
            return;
        }

        var mousePos = e.GetPosition(DockContents);
        var mouseX = IsVertical ? mousePos.Y : mousePos.X;
        const double influenceRadius = DockMetrics.InfluenceRadius;
        const double maxScale = DockMetrics.HoverScale;

        _currentMagnifiedBuffer.Clear();
        var currentMagnified = _currentMagnifiedBuffer;

        foreach (var button in GetAllDockButtons())
        {
            var icon = GetButtonIcon(button);
            if (icon is null) continue;

            var (scale, _) = GetIconTransforms(icon);
            icon.RenderTransformOrigin = IconScaleOrigin;
            var buttonPos = button.TranslatePoint(new Point(button.ActualWidth / 2, button.ActualHeight / 2), DockContents);
            var distance = Math.Abs(mouseX - (IsVertical ? buttonPos.Y : buttonPos.X));

            double targetScale = 1.0;
            if (distance < influenceRadius)
            {
                var factor = Math.Cos(distance / influenceRadius * (Math.PI / 2));
                targetScale = 1.0 + (maxScale - 1.0) * factor * factor;
            }

            scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            scale.ScaleX = targetScale;
            scale.ScaleY = targetScale;

            if (targetScale > 1.005)
            {
                currentMagnified.Add(icon);
            }
        }

        foreach (var icon in _magnifiedIcons)
        {
            if (!currentMagnified.Contains(icon))
            {
                var (scale, _) = GetIconTransforms(icon);
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                scale.ScaleX = 1.0;
                scale.ScaleY = 1.0;
            }
        }

        _magnifiedIcons.Clear();
        foreach (var icon in currentMagnified)
        {
            _magnifiedIcons.Add(icon);
        }
    }

    private void DockSurface_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e) => _autoHideController?.OnMouseEnterDock();

    private void DockSurface_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        ResetMagnification();
        _autoHideController?.OnMouseLeaveDock();
    }

    private void DockEdgeTrigger_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e) => _autoHideController?.OnMouseEnterEdge();
    private void DockEdgeTrigger_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e) => _autoHideController?.OnMouseLeaveEdge();

    private void ResetMagnification()
    {
        if (_magnifiedIcons.Count == 0) return;
        var duration = _preferences.Current.AnimationsEnabled && SystemParameters.ClientAreaAnimation
            ? TimeSpan.FromMilliseconds(120) : TimeSpan.Zero;
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };

        foreach (var icon in _magnifiedIcons)
        {
            var (scale, _) = GetIconTransforms(icon);
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1.0, duration) { EasingFunction = easing });
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1.0, duration) { EasingFunction = easing });
        }

        _magnifiedIcons.Clear();
    }
}
