using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace BD2ModManager.Gui.Controls;

/// <summary>
/// 固定在自身中心绘制的加载弧，避免 XAML 变换在布局阶段产生偏移。
/// 只负责显示；是否可见、角度更新由 MainWindow 控制。
/// </summary>
public sealed class LoadingSpinner : Control
{
    public static readonly StyledProperty<double> AngleProperty =
        AvaloniaProperty.Register<LoadingSpinner, double>(nameof(Angle));

    private static readonly IBrush SpinnerBrush =
        new SolidColorBrush(Color.Parse("#2563EB"));

    static LoadingSpinner()
    {
        AffectsRender<LoadingSpinner>(AngleProperty);
    }

    public double Angle
    {
        get => GetValue(AngleProperty);
        set => SetValue(AngleProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var size = Math.Min(Bounds.Width, Bounds.Height);
        if (size <= 8) return;

        var center = new Point(Bounds.Width / 2, Bounds.Height / 2);
        var radius = size / 2 - 4;
        var start = (Angle - 90) * Math.PI / 180;
        var sweep = 78 * Math.PI / 180;

        var p1 = new Point(
            center.X + radius * Math.Cos(start),
            center.Y + radius * Math.Sin(start));
        var p2 = new Point(
            center.X + radius * Math.Cos(start + sweep),
            center.Y + radius * Math.Sin(start + sweep));

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(p1, isFilled: false);
            ctx.ArcTo(p2, new Size(radius, radius), 0, false, SweepDirection.Clockwise);
            ctx.EndFigure(isClosed: false);
        }

        var pen = new Pen(SpinnerBrush, 4)
        {
            LineCap = PenLineCap.Round
        };
        context.DrawGeometry(null, pen, geometry);
    }
}
