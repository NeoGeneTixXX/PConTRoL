using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
    private readonly bool _popupWasVisible;
    private bool _loading = true;

    public SettingsWindow(UserSettings current, PopupWindow popup)
    {
        InitializeComponent();
        _original = current.Clone();
        _edit = current.Clone();
        _popup = popup;
        _popupWasVisible = popup.IsVisible;

        MainFolderBox.Text = _edit.MainFolder;
        TestModeBox.IsChecked = _edit.TestMode;
        TestFolderBox.Text = _edit.TestFolder;
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
            if (DialogResult != true) _popup.ApplyLook(_original);
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
        OpacityText.Text = $"{OpacitySlider.Value:0} %";
        _popup.ApplyLook(_edit);
    }

    private void OnOpacityChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (OpacityText != null) OpacityText.Text = $"{OpacitySlider.Value:0} %";
        Preview();
    }

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

    private void OnBrowseTest(object sender, RoutedEventArgs e) => Browse(TestFolderBox, "Testordner wählen");

    private void Browse(TextBox box, string title)
    {
        var dlg = new OpenFolderDialog { Title = title };
        if (Directory.Exists(box.Text)) dlg.InitialDirectory = box.Text;
        if (dlg.ShowDialog(this) == true) box.Text = dlg.FolderName;
    }

    private async void OnCreateTestCopy(object sender, RoutedEventArgs e)
    {
        var source = MainFolderBox.Text.Trim();
        var target = TestFolderBox.Text.Trim();
        if (!new AppPaths(source).IsValid)
        {
            Warn("Im Hauptordner fehlen _Input oder _NestResult.");
            return;
        }
        if (target.Length == 0)
        {
            Warn("Bitte zuerst einen Testordner wählen.");
            return;
        }

        var src = Path.GetFullPath(source).TrimEnd('\\') + "\\";
        var dst = Path.GetFullPath(target).TrimEnd('\\') + "\\";
        if (dst.StartsWith(src, StringComparison.OrdinalIgnoreCase) || src.StartsWith(dst, StringComparison.OrdinalIgnoreCase))
        {
            Warn("Testordner und Hauptordner dürfen nicht ineinander liegen.");
            return;
        }

        if (MessageBox.Show(this,
                $"Alle Dateien aus\n{source}\\_Input und _NestResult\nwerden nach\n{target}\nkopiert.\n\nVorhandene Dateien im Testordner werden NICHT überschrieben. Im Hauptordner wird nichts verändert.",
                "Testkopie anlegen", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return;

        IsEnabled = false;
        Cursor = Cursors.Wait;
        try
        {
            var (copied, skipped) = await Task.Run(() =>
            {
                var c = 0;
                var s = 0;
                foreach (var folder in new[] { AppPaths.InputFolderName, AppPaths.NestResultFolderName })
                {
                    var from = Path.Combine(source, folder);
                    foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
                    {
                        var to = Path.Combine(target, folder, Path.GetRelativePath(from, file));
                        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                        if (File.Exists(to)) { s++; continue; }
                        File.Copy(file, to, overwrite: false);
                        File.SetLastWriteTimeUtc(to, File.GetLastWriteTimeUtc(file));
                        c++;
                    }
                    Directory.CreateDirectory(Path.Combine(target, folder));
                }
                return (c, s);
            });
            MessageBox.Show(this, $"{copied} Dateien kopiert, {skipped} schon vorhanden.", "Testkopie", MessageBoxButton.OK, MessageBoxImage.Information);
            TestModeBox.IsChecked = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Warn("Kopieren fehlgeschlagen: " + ex.Message);
        }
        finally
        {
            IsEnabled = true;
            Cursor = null;
        }
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        _edit.MainFolder = MainFolderBox.Text.Trim();
        _edit.TestMode = TestModeBox.IsChecked == true;
        _edit.TestFolder = TestFolderBox.Text.Trim();
        _edit.StartWithWindows = AutostartBox.IsChecked == true;
        Preview();

        if (_edit.TestMode && _edit.TestFolder.Length == 0)
        {
            Warn("Testmodus ist an, aber es ist kein Testordner gewählt.");
            return;
        }
        if (_edit.TestMode && string.Equals(Path.GetFullPath(_edit.TestFolder).TrimEnd('\\'),
                Path.GetFullPath(_edit.MainFolder.Length > 0 ? _edit.MainFolder : ".").TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
        {
            Warn("Der Testordner darf nicht der Hauptordner sein.");
            return;
        }

        var root = _edit.EffectiveRoot;
        if (root.Length > 0 && !new AppPaths(root).IsValid &&
            MessageBox.Show(this, $"In „{root}“ fehlen _Input oder _NestResult.\nTrotzdem speichern?", "Ordner prüfen",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        DialogResult = true;
    }

    private void Warn(string text) => MessageBox.Show(this, text, "PConTRoL", MessageBoxButton.OK, MessageBoxImage.Warning);
}
