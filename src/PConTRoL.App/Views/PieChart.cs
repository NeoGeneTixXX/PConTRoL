using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace PConTRoL.App.Views;

/// <summary>
/// Kreisdiagramm mit zwei Anteilen (Ausnutzung / Verschnitt). Bewusst ohne Diagramm-Bibliothek:
/// zwei Kreissegmente sind schnell gezeichnet, und die Single-File-EXE bleibt klein.
/// </summary>
public sealed class PieChart : FrameworkElement
{
    public static readonly DependencyProperty PercentProperty = DependencyProperty.Register(
        nameof(Percent), typeof(double), typeof(PieChart), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
        nameof(Fill), typeof(Brush), typeof(PieChart), new FrameworkPropertyMetadata(Brushes.SeaGreen, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty RestProperty = DependencyProperty.Register(
        nameof(Rest), typeof(Brush), typeof(PieChart), new FrameworkPropertyMetadata(Brushes.IndianRed, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Anteil des ersten Segments in Prozent (0–100), beginnt oben und läuft im Uhrzeigersinn.</summary>
    public double Percent
    {
        get => (double)GetValue(PercentProperty);
        set => SetValue(PercentProperty, value);
    }

    public Brush Fill
    {
        get => (Brush)GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    public Brush Rest
    {
        get => (Brush)GetValue(RestProperty);
        set => SetValue(RestProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 0) return;
        var r = size / 2;
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        var p = Math.Clamp(double.IsNaN(Percent) ? 0 : Percent, 0, 100);
        var separator = new Pen(Brushes.Black, 1.5);

        if (p <= 0 || p >= 100)
        {
            dc.DrawEllipse(p >= 100 ? Fill : Rest, separator, center, r, r);
        }
        else
        {
            dc.DrawGeometry(Fill, separator, Slice(center, r, 0, p / 100 * 360));
            dc.DrawGeometry(Rest, separator, Slice(center, r, p / 100 * 360, 360));
        }

        // Prozentwert der Ausnutzung mittig auf einem dunklen Ring (Donut-Optik, gut lesbar)
        var inner = r * 0.55;
        dc.DrawEllipse((Brush)(TryFindResource("PanelBg") ?? Brushes.Black), null, center, inner, inner);
        var text = new FormattedText(p.ToString("0.0", CultureInfo.GetCultureInfo("de-DE")) + " %",
            CultureInfo.GetCultureInfo("de-DE"), FlowDirection.LeftToRight, new Typeface("Segoe UI Semibold"),
            Math.Max(10, inner * 0.42), (Brush)(TryFindResource("Fg") ?? Brushes.White), VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(text, new Point(center.X - text.Width / 2, center.Y - text.Height / 2));
    }

    private static Geometry Slice(Point c, double r, double fromDeg, double toDeg)
    {
        static Point On(Point c, double r, double deg)
        {
            var rad = (deg - 90) * Math.PI / 180;
            return new Point(c.X + r * Math.Cos(rad), c.Y + r * Math.Sin(rad));
        }

        var figure = new PathFigure { StartPoint = c, IsClosed = true };
        figure.Segments.Add(new LineSegment(On(c, r, fromDeg), true));
        figure.Segments.Add(new ArcSegment(On(c, r, toDeg), new Size(r, r), 0, toDeg - fromDeg > 180,
            SweepDirection.Clockwise, true));
        var geometry = new PathGeometry([figure]);
        geometry.Freeze();
        return geometry;
    }
}
