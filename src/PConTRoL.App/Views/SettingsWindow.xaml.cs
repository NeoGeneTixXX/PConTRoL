using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using PConTRoL.App.Services;
using PConTRoL.Core;

namespace PConTRoL.App.Views;

public partial class SettingsWindow : Window
{
    private readonly UserSettings _original;
    private readonly UserSettings _edit;
    private readonly PopupWindow _popup;
    private readonly Action<double?> _previewMainScale;
    private readonly bool _popupWasVisible;
    private bool _loading = true;

    /// <param name="previewMainScale">Vorschau der Größe im Hauptfenster; null = gespeicherten Wert wiederherstellen.</param>
    public SettingsWindow(UserSettings current, PopupWindow popup, Action<double?> previewMainScale)
    {
        InitializeComponent();
        _original = current.Clone();
        _edit = current.Clone();
        _popup = popup;
        _previewMainScale = previewMainScale;
        _popupWasVisible = popup.IsVisible;

        MainFolderBox.Text = _edit.MainFolder;
        MainScaleSlider.Value = Math.Round(_edit.MainScale * 100);
        PopupScaleSlider.Value = Math.Round(_edit.PopupScale * 100);
        OpacitySlider.Value = Math.Round(_edit.PopupOpacity * 100);
        TopmostBox.IsChecked = _edit.PopupTopmost;
        AutostartBox.IsChecked = _edit.StartWithWindows;
        UpdateSwatches();
        _loading = false;

        // Live-Vorschau: Pop-up während der Einstellungen anzeigen.
        Loaded += (_, _) =>
        {
            if (!_popup.IsVisible)
            {
                _popup.Show();
                _popup.PlaceBottomRight();
            }
            Preview();
        };
        Closed += (_, _) =>
        {
            if (DialogResult != true)
            {
                _popup.ApplyLook(_original);
                _previewMainScale(null);
            }
            if (!_popupWasVisible) _popup.Hide();
        };
    }

    /// <summary>Die gespeicherten Einstellungen (nur gültig nach DialogResult == true).</summary>
    public UserSettings Result => _edit;

    private void Preview()
    {
        if (_loading) return;
        _edit.PopupOpacity = OpacitySlider.Value / 100.0;
        _edit.PopupTopmost = TopmostBox.IsChecked == true;
        _edit.MainScale = MainScaleSlider.Value / 100.0;
        _edit.PopupScale = PopupScaleSlider.Value / 100.0;
        OpacityText.Text = $"{OpacitySlider.Value:0} %";
        MainScaleText.Text = $"{MainScaleSlider.Value:0} %";
        PopupScaleText.Text = $"{PopupScaleSlider.Value:0} %";
        _popup.ApplyLook(_edit);
        _previewMainScale(_edit.MainScale);
    }

    private void OnOpacityChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (OpacityText != null) OpacityText.Text = $"{OpacitySlider.Value:0} %";
        Preview();
    }

    private void OnScaleChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => Preview();

    private void OnLookChanged(object sender, RoutedEventArgs e) => Preview();

    private void OnPickColor(object sender, RoutedEventArgs e)
    {
        var tag = (string)((Button)sender).Tag;
        var current = tag switch
        {
            "bg" => _edit.PopupBackground,
            "fg" => _edit.PopupText,
            _ => _edit.PopupAccent,
        };
        var c = PopupWindow.ParseColor(current, Colors.Gray);
        using var dlg = new System.Windows.Forms.ColorDialog
        {
            Color = System.Drawing.Color.FromArgb(c.R, c.G, c.B),
            FullOpen = true,
            AnyColor = true,
        };
        if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;

        var hex = $"#FF{dlg.Color.R:X2}{dlg.Color.G:X2}{dlg.Color.B:X2}";
        switch (tag)
        {
            case "bg": _edit.PopupBackground = hex; break;
            case "fg": _edit.PopupText = hex; break;
            default: _edit.PopupAccent = hex; break;
        }
        UpdateSwatches();
        Preview();
    }

    private void UpdateSwatches()
    {
        Paint(BgButton, _edit.PopupBackground);
        Paint(FgButton, _edit.PopupText);
        Paint(AccentButton, _edit.PopupAccent);
    }

    private static void Paint(Button button, string hex)
    {
        var c = PopupWindow.ParseColor(hex, Colors.Gray);
        button.Background = new SolidColorBrush(c);
        var light = 0.299 * c.R + 0.587 * c.G + 0.114 * c.B > 140;
        button.Foreground = light ? Brushes.Black : Brushes.White;
        button.Content = "#" + hex.TrimStart('#')[^6..];
    }

    private void OnBrowseMain(object sender, RoutedEventArgs e) => Browse(MainFolderBox, "Hauptordner wählen (enthält _Input und _NestResult)");

    private void Browse(TextBox box, string title)
    {
        var dlg = new OpenFolderDialog { Title = title };
        if (Directory.Exists(box.Text)) dlg.InitialDirectory = box.Text;
        if (dlg.ShowDialog(this) == true) box.Text = dlg.FolderName;
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        _edit.MainFolder = MainFolderBox.Text.Trim();
        _edit.StartWithWindows = AutostartBox.IsChecked == true;
        Preview();

        var root = _edit.MainFolder;
        if (root.Length > 0 && !new AppPaths(root).IsValid &&
            MessageBox.Show(this, $"In „{root}“ fehlen _Input oder _NestResult.\nTrotzdem speichern?", "Ordner prüfen",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        DialogResult = true;
    }
}
