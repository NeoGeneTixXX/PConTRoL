using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using PConTRoL.App.Services;
using PConTRoL.Core;
using PConTRoL.Core.Database;

namespace PConTRoL.App.Views;

public partial class SettingsWindow : Window
{
    private readonly UserSettings _original;
    private readonly UserSettings _edit;
    private readonly PopupWindow _popup;
    private readonly Action<double?> _previewMainScale;
    private readonly bool _popupWasVisible;
    private readonly UpdateService? _updates;
    private readonly QueueController? _controller;
    private bool _loading = true;

    /// <param name="previewMainScale">Vorschau der Größe im Hauptfenster; null = gespeicherten Wert wiederherstellen.</param>
    /// <param name="updates">Für „Nach Updates suchen“; null = nicht verfügbar.</param>
    /// <param name="controller">Für die Kanban-Kacheln (gespeicherter Hauptordner); null = kein Hauptordner.</param>
    public SettingsWindow(UserSettings current, PopupWindow popup, Action<double?> previewMainScale,
        UpdateService? updates = null, QueueController? controller = null)
    {
        InitializeComponent();
        _original = current.Clone();
        _edit = current.Clone();
        _popup = popup;
        _previewMainScale = previewMainScale;
        _updates = updates;
        _controller = controller;
        _popupWasVisible = popup.IsVisible;
        ShowUpdateState();
        BuildKanbanRows();

        MainFolderBox.Text = _edit.MainFolder;
        MainScaleSlider.Value = Math.Round(_edit.MainScale * 100);
        PopupScaleSlider.Value = Math.Round(_edit.PopupScale * 100);
        OpacitySlider.Value = Math.Round(_edit.PopupOpacity * 100);
        TopmostBox.IsChecked = _edit.PopupTopmost;
        AutostartBox.IsChecked = _edit.StartWithWindows;
        DatabaseBox.Password = _edit.DatabaseConnection;
        ShowDatabaseState();
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

    // ---------- Kanban-Kacheln (wirken sofort) ----------

    /// <summary>Wurde an den Kacheln etwas geändert? Dann liest das Hauptfenster danach gleich neu ein.</summary>
    public bool KanbanChanged { get; private set; }

    private void BuildKanbanRows()
    {
        KanbanPanel.Children.Clear();
        if (_controller == null)
        {
            NewTileButton.IsEnabled = false;
            KanbanHint.Text = "Kanban-Kacheln gibt es erst, wenn ein Hauptordner gespeichert ist.";
            return;
        }

        IReadOnlyList<Core.Kanban.KanbanTile> tiles;
        try
        {
            tiles = _controller.KanbanTiles();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            KanbanHint.Text = "Kacheln konnten nicht gelesen werden: " + ex.Message;
            return;
        }

        foreach (var tile in tiles)
        {
            var grid = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            for (var i = 0; i < 3; i++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var name = new TextBlock
            {
                Text = $"{tile.Name}   ({Plural(tile.Files.Count)})",
                FontSize = 15,
                FontWeight = FontWeights.Bold,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                ToolTip = $"Ordner: {Path.Combine(_controller.Paths.Kanban, tile.Folder)}\n" + string.Join("\n", tile.Files),
            };
            Grid.SetColumn(name, 1);
            grid.Children.Add(name);

            var id = tile.Id;
            var currentName = tile.Name;
            AddButton(grid, 2, "Umbenennen …", async () =>
            {
                var newName = InputDialog.Ask(this, "Kachel umbenennen", "Neuer Name (= Name der Gruppe an der Maschine):", currentName);
                if (newName != null) await RunKanban(c => c.RenameKanbanTile(id, newName));
            });
            var existing = tile.Files.ToList();
            AddButton(grid, 3, "Platten hinzufügen …", async () =>
            {
                var choice = AskPlates(currentName, Path.Combine(_controller.Paths.Kanban, tile.Folder), existing);
                if (choice == null) return;
                await RunKanban(c => choice.Files != null
                    ? c.AddToKanbanTile(id, choice.Files)
                    : c.AddRepeatedToKanbanTile(id, choice.Single!, choice.Count));
            });
            AddButton(grid, 4, "Auflösen", async () =>
            {
                var answer = MessageBox.Show(this,
                    $"Kachel „{currentName}“ auflösen?\n\nGelöscht wird nichts: Die {Plural(tile.Files.Count)} kommen in den Vorrat " +
                    $"({Path.Combine(_controller.Paths.NestResult, tile.Folder)}).",
                    "Kachel auflösen", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (answer == MessageBoxResult.Yes) await RunKanban(c => c.RemoveKanbanTile(id));
            });
            KanbanPanel.Children.Add(grid);
        }
    }

    private static void AddButton(Grid grid, int column, string text, Func<Task> action)
    {
        var button = new Button { Content = text, FontSize = 14, Padding = new Thickness(10, 3, 10, 3) };
        button.Click += async (_, _) => await action();
        Grid.SetColumn(button, column);
        grid.Children.Add(button);
    }

    private async void OnNewTile(object sender, RoutedEventArgs e)
    {
        if (_controller == null) return;
        var name = InputDialog.Ask(this, "Neue Kanban-Kachel", "Name der Kachel (= Name der Gruppe an der Maschine):", "Neue Kachel");
        if (name == null) return;
        var choice = AskPlates(name, Path.Combine(_controller.Paths.Kanban, name), []);
        if (choice == null) return;
        await RunKanban(c => choice.Files != null
            ? c.CreateKanbanTile(name, choice.Files)
            : c.CreateKanbanTileRepeated(name, choice.Single!, choice.Count));
    }

    /// <summary>Entweder mehrere verschiedene Platten (Files) oder eine Platte (Single) mehrmals (Count).</summary>
    private sealed record PlateChoice(List<string>? Files, string? Single, int Count);

    /// <summary>Fragt „Unterschiedliche Platten oder eine Platte X-mal?“, lässt auswählen und bestätigen.</summary>
    private PlateChoice? AskPlates(string tileName, string folder, IReadOnlyList<string> existingFiles)
    {
        var mode = ChoiceDialog.Ask(this, $"Platten für „{tileName}“", "Unterschiedliche Platten oder eine Platte X-mal?",
            ("Unterschiedliche Platten", "Mehrere .hop-Dateien auswählen; jede kommt einmal in die Kachel."),
            ("Eine Platte X-mal", "Eine .hop-Datei auswählen und angeben, wie oft sie gebraucht wird. Die Dateien heißen dann …_01, …_02, …_03 usw."));
        if (mode < 0) return null;

        if (mode == 0)
        {
            var files = PickHops($"Platten für „{tileName}“ auswählen", multiple: true);
            return files != null && ConfirmMove(files.Count, folder) ? new PlateChoice(files, null, 0) : null;
        }

        var file = PickHops($"Eine Platte für „{tileName}“ auswählen", multiple: false)?.FirstOrDefault();
        if (file == null) return null;
        var text = InputDialog.Ask(this, "Wie oft?", $"Wie oft wird „{Path.GetFileName(file)}“ gebraucht?", "2");
        if (text == null) return null;
        if (!int.TryParse(text, out var count) || count < 1 || count > QueueController.MaxKanbanRepeat)
        {
            MessageBox.Show(this, $"Bitte eine Zahl zwischen 1 und {QueueController.MaxKanbanRepeat} eingeben.", "Kanban",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return null;
        }

        var names = QueueController.RepeatedNames(Path.GetFileName(file), count, existingFiles);
        var range = names.Count == 1 ? names[0] : $"{names[0]} bis {names[^1]}";
        var ok = MessageBox.Show(this,
            $"„{Path.GetFileName(file)}“ (mit .lbl und .jpg) wird nach\n{folder}\nverschoben und dort {count}-mal abgelegt:\n{range}\n\n" +
            "An die Maschine gehen immer Kopien.",
            "Kanban", MessageBoxButton.OKCancel, MessageBoxImage.Information) == MessageBoxResult.OK;
        return ok ? new PlateChoice(null, file, count) : null;
    }

    private List<string>? PickHops(string title, bool multiple)
    {
        var dlg = new OpenFileDialog
        {
            Title = title,
            Filter = "Nest-Programme (*.hop)|*.hop",
            Multiselect = multiple,
        };
        if (_controller != null && Directory.Exists(_controller.Paths.NestResult)) dlg.InitialDirectory = _controller.Paths.NestResult;
        return dlg.ShowDialog(this) == true && dlg.FileNames.Length > 0 ? dlg.FileNames.ToList() : null;
    }

    private bool ConfirmMove(int count, string folder) =>
        MessageBox.Show(this,
            $"{Plural(count)} (mit .lbl und .jpg) werden nach\n{folder}\nverschoben.\n\nDort liegen sie geschützt; an die Maschine gehen immer Kopien.",
            "Kanban", MessageBoxButton.OKCancel, MessageBoxImage.Information) == MessageBoxResult.OK;

    private async Task RunKanban(Func<QueueController, OperationResult> op)
    {
        if (_controller == null) return;
        var controller = _controller;
        KanbanPanel.IsEnabled = NewTileButton.IsEnabled = false;
        try
        {
            var result = await Task.Run(() => op(controller));
            if (!result.Success)
                MessageBox.Show(this, result.Message, "Nicht möglich", MessageBoxButton.OK, MessageBoxImage.Warning);
            else if (result.Changed)
                KanbanChanged = true;
        }
        finally
        {
            KanbanPanel.IsEnabled = NewTileButton.IsEnabled = true;
            BuildKanbanRows();
        }
    }

    private static string Plural(int n) => n == 1 ? "1 Platte" : $"{n} Platten";

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

    private void ShowDatabaseState()
    {
        var text = DatabaseBox.Password;
        if (string.IsNullOrWhiteSpace(text))
        {
            DatabaseStatus.Text = "Keine Datenbank eingerichtet.";
            return;
        }
        var error = DatabaseConnection.TryBuild(text, out _);
        DatabaseStatus.Text = error ?? DatabaseConnection.Describe(text) + (text == _original.DatabaseConnection ? "" : "  ·  noch nicht gespeichert");
    }

    private void OnDatabaseChanged(object sender, RoutedEventArgs e)
    {
        if (!_loading) ShowDatabaseState();
    }

    private async void OnTestDatabase(object sender, RoutedEventArgs e)
    {
        var error = DatabaseConnection.TryBuild(DatabaseBox.Password, out var cs);
        if (error != null)
        {
            DatabaseStatus.Text = error;
            return;
        }
        var button = (Button)sender;
        button.IsEnabled = false;
        DatabaseStatus.Text = "Verbinde …";
        try
        {
            var version = await Task.Run(() => new SharedDatabase(cs).TestAsync());
            DatabaseStatus.Text = $"Verbindung klappt: {DatabaseConnection.Describe(DatabaseBox.Password)}  ·  "
                + (version != null ? $"Update in der Datenbank: Version {version}" : "noch kein Update in der Datenbank");
        }
        catch (Exception ex)
        {
            DatabaseStatus.Text = "Keine Verbindung: " + UpdateService.Describe(ex, "der Datenbank")
                + $"  (Details in {SettingsStore.Folder}\\fehler.log)";
            ErrorLog.Write($"Datenbank-Test fehlgeschlagen ({DatabaseConnection.Describe(DatabaseBox.Password)})", ex);
        }
        finally
        {
            button.IsEnabled = true;
        }
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        _edit.MainFolder = MainFolderBox.Text.Trim();
        var dbError = DatabaseBox.Password.Trim().Length > 0 ? DatabaseConnection.TryBuild(DatabaseBox.Password, out _) : null;
        if (dbError != null)
        {
            MessageBox.Show(this, dbError, "Datenbank", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        _edit.DatabaseConnection = DatabaseBox.Password;
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
