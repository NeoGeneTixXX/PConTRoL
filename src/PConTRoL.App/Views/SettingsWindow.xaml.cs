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
    private readonly UpdateService? _updates;
    private bool _loading = true;

    /// <param name="previewMainScale">Vorschau der Größe im Hauptfenster; null = gespeicherten Wert wiederherstellen.</param>
    /// <param name="updates">Für „Nach Updates suchen“; null = nicht verfügbar.</param>
    public SettingsWindow(UserSettings current, PopupWindow popup, Action<double?> previewMainScale, UpdateService? updates = null)
    {
        InitializeComponent();
        _original = current.Clone();
        _edit = current.Clone();
        _popup = popup;
        _previewMainScale = previewMainScale;
        _updates = updates;
        _popupWasVisible = popup.IsVisible;
        ShowUpdateState();

        MainFolderBox.Text = _edit.MainFolder;
        MainScaleSlider.Value = Math.Round(_edit.MainScale * 100);
        PopupScaleSlider.Value = Math.Round(_edit.PopupScale * 100);
        OpacitySlider.Value = Math.Round(_edit.PopupOpacity * 100);
        TopmostBox.IsChecked = _edit.PopupTopmost;
        AutostartBox.IsChecked = _edit.StartWithWindows;
        _edit.PartColumns = PartColumnCatalog.Normalize(_edit.PartColumns);
        BuildPartColumnRows();
        UpdateSwatches();
        _loading = false;
        ApplyInitialSize(current);

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

    // ---------- Größe ----------

    /// <summary>Größe beim Schließen (für das nächste Öffnen), egal ob gespeichert oder abgebrochen.</summary>
    public Size LastSize { get; private set; }

    /// <summary>
    /// Inhalt folgt der eingestellten Programmgröße (wie beim Öffnen gespeichert). Die Fenstergröße ist frei
    /// ziehbar und wird gemerkt; ohne gemerkte Größe passt sich die Höhe dem Inhalt an.
    /// </summary>
    private void ApplyInitialSize(UserSettings current)
    {
        var scale = UserSettings.ClampScale(current.MainScale);
        if (Math.Abs(scale - 1) > 0.001)
        {
            SettingsRoot.LayoutTransform = new ScaleTransform(scale, scale);
            Width *= scale;
            MinWidth *= scale;
        }

        var area = SystemParameters.WorkArea;
        MaxHeight = area.Height;
        if (double.IsFinite(current.SettingsWidth) && double.IsFinite(current.SettingsHeight))
        {
            SizeToContent = SizeToContent.Manual;
            Width = Math.Clamp(current.SettingsWidth, MinWidth, Math.Max(MinWidth, area.Width));
            Height = Math.Clamp(current.SettingsHeight, MinHeight, Math.Max(MinHeight, area.Height));
        }
        else
        {
            Width = Math.Min(Width, area.Width);
        }

        // WPF stellt SizeToContent auf Manual, sobald man das Fenster von Hand zieht – nur dann merken.
        Closing += (_, _) => LastSize = SizeToContent == SizeToContent.Manual
            ? new Size(ActualWidth, ActualHeight)
            : new Size(double.NaN, double.NaN);
    }

    // ---------- Update ----------

    /// <summary>Nach dem Schließen: Update einspielen (nur zusammen mit DialogResult == true).</summary>
    public bool InstallUpdateRequested { get; private set; }

    private void ShowUpdateState()
    {
        VersionLabel.Text = "Version " + UpdateService.CurrentVersion;
        if (_updates == null || !_updates.IsEnabled)
        {
            CheckUpdateButton.IsEnabled = false;
            SetUpdateStatus("Diese Kopie ist nicht per Setup installiert und kann sich nicht selbst aktualisieren.", "FgDim");
            return;
        }
        if (_updates.Pending is { } pending)
            ShowReady(pending.Version.ToString());
    }

    private async void OnCheckUpdate(object sender, RoutedEventArgs e)
    {
        if (_updates == null) return;
        CheckUpdateButton.IsEnabled = false;
        SetUpdateStatus("Suche nach Updates …", "FgDim");
        try
        {
            var result = await _updates.CheckNowAsync();
            switch (result.State)
            {
                case UpdateCheckState.Ready:
                    ShowReady(result.Version ?? "?");
                    break;
                case UpdateCheckState.UpToDate:
                    SetUpdateStatus("✓ PConTRoL ist aktuell.", "Fg");
                    break;
                case UpdateCheckState.NotInstalled:
                    SetUpdateStatus("Diese Kopie ist nicht per Setup installiert und kann sich nicht selbst aktualisieren.", "FgDim");
                    break;
                default:
                    SetUpdateStatus($"Prüfung fehlgeschlagen: {result.Error}\nDetails: {SettingsStore.Folder}\\update.log", "Danger");
                    break;
            }
        }
        finally
        {
            CheckUpdateButton.IsEnabled = _updates.IsEnabled;
        }
    }

    private void ShowReady(string version)
    {
        SetUpdateStatus($"Version {version} ist heruntergeladen.", "Accent");
        InstallUpdateButton.Visibility = Visibility.Visible;
    }

    private void SetUpdateStatus(string text, string brushKey)
    {
        UpdateStatus.Text = text;
        UpdateStatus.Foreground = (Brush)FindResource(brushKey);
    }

    /// <summary>Wie „Speichern“, danach spielt das Hauptfenster das Update ein.</summary>
    private void OnInstallUpdate(object sender, RoutedEventArgs e)
    {
        InstallUpdateRequested = true;
        OnSave(sender, e);
        if (DialogResult != true) InstallUpdateRequested = false;
    }

    // ---------- Teileliste ----------

    /// <summary>Eine Zeile je Spalte: Originalname | eigener Name | anzeigen | ▲ ▼.</summary>
    private void BuildPartColumnRows()
    {
        PartColumnsPanel.Children.Clear();
        var columns = _edit.PartColumns;
        for (var i = 0; i < columns.Count; i++)
        {
            var setting = columns[i];
            var index = i;
            var grid = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var original = new TextBlock
            {
                Text = PartColumnCatalog.Find(setting.Key)?.DefaultHeader ?? setting.Key,
                Style = (Style)FindResource("Label"),
            };
            var name = new TextBox { Text = setting.Header, VerticalAlignment = VerticalAlignment.Center };
            name.TextChanged += (_, _) => setting.Header = name.Text;
            var visible = new CheckBox { Content = "anzeigen", IsChecked = setting.Visible, Margin = new Thickness(12, 0, 6, 0) };
            visible.Click += (_, _) => setting.Visible = visible.IsChecked == true;
            var up = new Button { Content = "▲", Padding = new Thickness(10, 2, 10, 2), FontSize = 14, IsEnabled = index > 0, ToolTip = "Weiter nach links" };
            up.Click += (_, _) => MovePartColumn(index, -1);
            var down = new Button { Content = "▼", Padding = new Thickness(10, 2, 10, 2), FontSize = 14, IsEnabled = index < columns.Count - 1, ToolTip = "Weiter nach rechts" };
            down.Click += (_, _) => MovePartColumn(index, +1);

            Grid.SetColumn(name, 1);
            Grid.SetColumn(visible, 2);
            Grid.SetColumn(up, 3);
            Grid.SetColumn(down, 4);
            grid.Children.Add(original);
            grid.Children.Add(name);
            grid.Children.Add(visible);
            grid.Children.Add(up);
            grid.Children.Add(down);
            PartColumnsPanel.Children.Add(grid);
        }
    }

    private void MovePartColumn(int index, int delta)
    {
        var columns = _edit.PartColumns;
        var target = index + delta;
        if (target < 0 || target >= columns.Count) return;
        (columns[index], columns[target]) = (columns[target], columns[index]);
        BuildPartColumnRows();
    }

    private void OnResetPartColumns(object sender, RoutedEventArgs e)
    {
        _edit.PartColumns = PartColumnCatalog.Defaults();
        BuildPartColumnRows();
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
        _edit.PartColumns = PartColumnCatalog.Normalize(_edit.PartColumns);
        Preview();

        var root = _edit.MainFolder;
        if (root.Length > 0 && !new AppPaths(root).IsValid &&
            MessageBox.Show(this, $"In „{root}“ fehlen _Input oder _NestResult.\nTrotzdem speichern?", "Ordner prüfen",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        DialogResult = true;
    }
}
