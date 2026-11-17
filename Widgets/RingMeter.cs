using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace OpenTaskbar.Widgets;

/// <summary>
/// A circular progress ring with a centered caption, drawn directly so it stays crisp at any DPI.
/// </summary>
public sealed class RingMeter : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(RingMeter),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender, null, CoerceValue));

    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(RingMeter),
        new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeThicknessProperty = DependencyProperty.Register(
        nameof(StrokeThickness), typeof(double), typeof(RingMeter),
        new FrameworkPropertyMetadata(3.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrackBrushProperty = DependencyProperty.Register(
        nameof(TrackBrush), typeof(Brush), typeof(RingMeter),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ValueBrushProperty = DependencyProperty.Register(
        nameof(ValueBrush), typeof(Brush), typeof(RingMeter),
        new FrameworkPropertyMetadata(Brushes.DeepSkyBlue, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ForegroundProperty = DependencyProperty.Register(
        nameof(Foreground), typeof(Brush), typeof(RingMeter),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FontSizeProperty = DependencyProperty.Register(
        nameof(FontSize), typeof(double), typeof(RingMeter),
        new FrameworkPropertyMetadata(11.0, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly Typeface CaptionFace = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    private Pen? _trackPen;
    private Brush? _trackPenBrush;
    private double _trackPenThickness;
    private Pen? _valuePen;
    private Brush? _valuePenBrush;
    private double _valuePenThickness;

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public double StrokeThickness { get => (double)GetValue(StrokeThicknessProperty); set => SetValue(StrokeThicknessProperty, value); }
    public Brush TrackBrush { get => (Brush)GetValue(TrackBrushProperty); set => SetValue(TrackBrushProperty, value); }
    public Brush ValueBrush { get => (Brush)GetValue(ValueBrushProperty); set => SetValue(ValueBrushProperty, value); }
    public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    public double FontSize { get => (double)GetValue(FontSizeProperty); set => SetValue(FontSizeProperty, value); }

    private static object CoerceValue(DependencyObject d, object value) =>
        value is double v && double.IsFinite(v) ? Math.Clamp(v, 0, 1) : 0.0;

    protected override void OnRender(DrawingContext context)
    {
        var size = Math.Min(ActualWidth, ActualHeight);
        if (size <= StrokeThickness * 2) return;
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        var radius = (size - StrokeThickness) / 2;

        var trackPen = GetTrackPen(TrackBrush, StrokeThickness);
        context.DrawEllipse(null, trackPen, center, radius, radius);

        if (Value > 0.001)
        {
            var valuePen = GetValuePen(ValueBrush, StrokeThickness);
            if (Value >= 0.999)
            {
                context.DrawEllipse(null, valuePen, center, radius, radius);
            }
            else
            {
                // Clockwise from 12 o'clock.
                var angle = Value * 2 * Math.PI;
                var start = new Point(center.X, center.Y - radius);
                var end = new Point(center.X + radius * Math.Sin(angle), center.Y - radius * Math.Cos(angle));
                var geometry = new StreamGeometry();
                using (var g = geometry.Open())
                {
                    g.BeginFigure(start, false, false);
                    g.ArcTo(end, new Size(radius, radius), 0, Value > 0.5, SweepDirection.Clockwise, true, false);
                }

                geometry.Freeze();
                context.DrawGeometry(null, valuePen, geometry);
            }
        }

        var pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        FormattedText? caption = null;
        if (!string.IsNullOrEmpty(Caption))
        {
            caption = new FormattedText(Caption, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, LabelFace,
                Math.Max(6, FontSize * 0.62), CaptionBrush, pixelsPerDip);
        }

        if (!string.IsNullOrEmpty(Text))
        {
            var text = new FormattedText(Text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, CaptionFace,
                FontSize, Foreground, pixelsPerDip);
            // Stack the caption over the value and center the pair inside the ring.
            var totalHeight = text.Height + (caption?.Height ?? 0) - (caption is null ? 0 : 3);
            var top = center.Y - totalHeight / 2;
            if (caption is not null)
            {
                context.DrawText(caption, new Point(center.X - caption.Width / 2, top));
                top += caption.Height - 3;
            }

            context.DrawText(text, new Point(center.X - text.Width / 2, top));
        }
    }

    public static readonly DependencyProperty CaptionProperty = DependencyProperty.Register(
        nameof(Caption), typeof(string), typeof(RingMeter),
        new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty CaptionBrushProperty = DependencyProperty.Register(
        nameof(CaptionBrush), typeof(Brush), typeof(RingMeter),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly Typeface LabelFace = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    public string Caption { get => (string)GetValue(CaptionProperty); set => SetValue(CaptionProperty, value); }
    public Brush CaptionBrush { get => (Brush)GetValue(CaptionBrushProperty); set => SetValue(CaptionBrushProperty, value); }

    private Pen GetTrackPen(Brush brush, double thickness)
    {
        if (_trackPen is not null && ReferenceEquals(_trackPenBrush, brush) && _trackPenThickness == thickness)
            return _trackPen;
        var pen = new Pen(brush, thickness);
        if (brush.IsFrozen) pen.Freeze();
        _trackPen = pen;
        _trackPenBrush = brush;
        _trackPenThickness = thickness;
        return pen;
    }

    private Pen GetValuePen(Brush brush, double thickness)
    {
        if (_valuePen is not null && ReferenceEquals(_valuePenBrush, brush) && _valuePenThickness == thickness)
            return _valuePen;
        var pen = new Pen(brush, thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        if (brush.IsFrozen) pen.Freeze();
        _valuePen = pen;
        _valuePenBrush = brush;
        _valuePenThickness = thickness;
        return pen;
    }
}
