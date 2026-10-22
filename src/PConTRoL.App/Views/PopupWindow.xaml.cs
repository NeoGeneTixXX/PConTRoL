using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PConTRoL.App.Services;
using PConTRoL.App.ViewModels;

namespace PConTRoL.App.Views;

/// <summary>Kleines Fenster unten rechts: laufender Auftrag + die nächsten 10.</summary>
public partial class PopupWindow : Window
{
    private bool _userMoved;

    public PopupWindow(QueueStatus status)
    {
        InitializeComponent();
        DataContext = status;
        Header.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState != MouseButtonState.Pressed) return;
            _userMoved = true;
            DragMove();
        };
        SizeChanged += (_, _) => PlaceBottomRight();
    }

    public event EventHandler? OpenMainRequested;

    /// <summary>Farben, Transparenz und "Immer im Vordergrund" übernehmen (auch für die Live-Vorschau).</summary>
    public void ApplyLook(UserSettings s)
    {
        var bg = ParseColor(s.PopupBackground, Color.FromRgb(0x26, 0x26, 0x26));
        var fg = new SolidColorBrush(ParseColor(s.PopupText, Colors.WhiteSmoke));
        var accent = new SolidColorBrush(ParseColor(s.PopupAccent, Color.FromRgb(0xF2, 0xC2, 0x30)));

        Root.Background = new SolidColorBrush(bg);
        Root.BorderBrush = accent;
        Opacity = Math.Clamp(s.PopupOpacity, 0.3, 1.0);
        Topmost = s.PopupTopmost;

        foreach (var tb in new[] { HeaderText, CurrentName, CurrentInfo, Footer }) tb.Foreground = fg;
        RunningLabel.Foreground = accent;
        NextLabel.Foreground = accent;
        foreach (var b in Header.Children.OfType<Button>()) b.Foreground = fg;
    }

    public void ShowAtCorner()
    {
        _userMoved = false;
        if (!IsVisible) Show();
        PlaceBottomRight();
        Activate();
    }

    public void PlaceBottomRight()
    {
        if (_userMoved || !IsVisible) return;
        var area = SystemParameters.WorkArea;
        Left = area.Right - ActualWidth - 12;
        Top = area.Bottom - ActualHeight - 12;
    }

    private void OnClose(object sender, RoutedEventArgs e) => Hide();

    private void OnOpenMain(object sender, RoutedEventArgs e) => OpenMainRequested?.Invoke(this, EventArgs.Empty);

    internal static Color ParseColor(string? text, Color fallback)
    {
        try
        {
            return text != null ? (Color)ColorConverter.ConvertFromString(text) : fallback;
        }
        catch (FormatException)
        {
            return fallback;
        }
    }
}
