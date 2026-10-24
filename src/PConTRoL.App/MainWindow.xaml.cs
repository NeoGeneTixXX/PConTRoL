using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PConTRoL.App.Services;
using PConTRoL.App.ViewModels;
using PConTRoL.App.Views;
using PConTRoL.Core;
using PConTRoL.Core.Automation;
using PConTRoL.Core.Layout;
using PConTRoL.Core.Logging;
using PConTRoL.Core.Statistics;
using PConTRoL.Core.Timing;

namespace PConTRoL.App;

public partial class MainWindow : Window, IJobListHost
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(3);
    private static readonly CultureInfo De = CultureInfo.GetCultureInfo("de-DE");

    private readonly DispatcherTimer _timer = new() { Interval = RefreshInterval };
    private readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly AutoFeeder _autoFeeder = new();
    private readonly Dictionary<DataGridColumn, string> _partColumnKeys = [];
    private string? _kanbanSignature;
    private string? _autoError;
    private UserSettings _settings;
    private QueueController? _controller;
    private StatisticsStore? _statistics;
    private RunTimeStore? _runTimes;
    private RunTimeEstimator? _estimator;
    private IReadOnlyList<RunTimeRecord>? _estimatorSource;
    private QueueSnapshot? _snapshot;
    private string? _error;
    private bool _refreshing;
    private bool _busy;
    private bool _fullScreen;
    private WindowState _stateBeforeFullScreen;
    private JobListControl? _detailsSource;

    public MainWindow(UserSettings settings, QueueStatus status, PopupWindow popup)
    {
        InitializeComponent();
        _settings = settings;
        Status = status;
        Popup = popup;
        Icon = IconLoader.WindowIcon();

        StockList.Host = this;
        MachineList.Host = this;
        StockList.Side = Side.Stock;
        MachineList.Side = Side.Machine;

        _timer.Tick += async (_, _) => await RefreshNow();
        _debounce.Tick += async (_, _) =>
        {
            _debounce.Stop();
            await RefreshNow();
        };

        RestorePlacement();
        Closing += OnClosing;
        StateChanged += (_, _) =>
        {
            if (WindowState == WindowState.Minimized) HideToTray();
        };

        VersionText.Text = "v" + UpdateService.CurrentVersion;
        ApplyScale();
        ApplyPartColumns();
        PartsGrid.ColumnReordered += (_, _) => CapturePartColumnOrder();

        ApplyRoot();
        _timer.Start();
    }

    public QueueStatus Status { get; }
    public PopupWindow Popup { get; }

    /// <summary>Wird von App beim Beenden gesetzt; sonst schließt das Fenster nur in den Tray.</summary>
    public bool AllowClose { get; set; }

    public UserSettings Settings => _settings;

    /// <summary>Läuft gerade eine Dateiaktion (Verschieben, Datum setzen …)? Dann kein Update einspielen.</summary>
    public bool IsBusy => _busy;

    public event EventHandler? HiddenToTray;

    /// <summary>Klick auf „Jetzt aktualisieren“ im Update-Hinweis oder in den Einstellungen.</summary>
    public event EventHandler? UpdateRequested;

    /// <summary>Für „Nach Updates suchen“ in den Einstellungen (von App gesetzt).</summary>
    public UpdateService? Updates { get; set; }

    /// <summary>Gemeinsame Datenbank als Update-Quelle (von App gesetzt).</summary>
    public DatabaseService? Database { get; set; }


    // ---------- Update-Hinweis ----------

    public void ShowUpdateAvailable(string version, string? notes)
    {
        UpdateTitle.Text = $"Neue Version {version}, jetzt aktualisieren?";
        UpdateNotes.Text = string.IsNullOrWhiteSpace(notes) ? "" : notes.Trim();
        UpdateNotes.Visibility = UpdateNotes.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateBanner.Visibility = Visibility.Visible;
    }

    private void OnUpdateNowClick(object sender, RoutedEventArgs e) => UpdateRequested?.Invoke(this, EventArgs.Empty);

    private void OnUpdateLaterClick(object sender, RoutedEventArgs e) => UpdateBanner.Visibility = Visibility.Collapsed;

    // ---------- Ordner / Aktualisierung ----------

    private void ApplyRoot()
    {
        foreach (var w in _watchers) w.Dispose();
        _watchers.Clear();
        _snapshot = null;

        var root = _settings.MainFolder;
        FolderText.Text = root.Length > 0 ? root : "Kein Hauptordner gewählt – bitte Einstellungen öffnen";

        StockList.ApplyColumnStates(_settings.Columns.GetValueOrDefault("Stock"));
        MachineList.ApplyColumnStates(_settings.Columns.GetValueOrDefault("Machine"));

        if (root.Length == 0)
        {
            _controller = null;
            _statistics = null;
            _runTimes = null;
            Render();
            return;
        }

        var paths = new AppPaths(root);
        _statistics = new StatisticsStore(paths.StatisticsFile);
        _runTimes = new RunTimeStore(paths.RunTimeFile);
        _controller = new QueueController(paths, new ActionLog(paths.LogFolder), statistics: _statistics, runTimes: _runTimes);
        StartWatchers(paths);
        _ = RefreshNow();
    }

    private void StartWatchers(AppPaths paths)
    {
        void Add(string path, bool recursive)
        {
            try
            {
                var w = new FileSystemWatcher(path)
                {
                    IncludeSubdirectories = recursive,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.DirectoryName,
                };
                FileSystemEventHandler handler = (_, _) => Dispatcher.BeginInvoke(() =>
                {
                    _debounce.Stop();
                    _debounce.Start();
                });
                w.Created += handler;
                w.Deleted += handler;
                w.Changed += handler;
                w.Renamed += (s, e) => handler(s, e);
                w.Error += (_, _) => { }; // Netzlaufwerk: Ausfälle fängt die regelmäßige Aktualisierung ab.
                w.EnableRaisingEvents = true;
                _watchers.Add(w);
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or PlatformNotSupportedException)
            {
                // Ordner (noch) nicht erreichbar – Abgleich alle 3 s läuft trotzdem.
            }
        }

        Add(paths.Input, false);
        Add(paths.NestResult, true);
    }

    public async Task RefreshNow()
    {
        var controller = _controller;
        if (controller == null || _refreshing || _busy || StockList.IsDragging || MachineList.IsDragging) return;
        _refreshing = true;
        try
        {
            _snapshot = await Task.Run(controller.Refresh);
            _error = null;
        }
        catch (Exception ex)
        {
            _error = ex.Message;
        }
        finally
        {
            _refreshing = false;
        }
        if (controller == _controller)
        {
            Render();
            await CheckAutoFeed();
        }
    }

    // ---------- Automatik ----------

    private async void OnAutoClick(object sender, RoutedEventArgs e)
    {
        if (_snapshot == null) return;
        var enable = !_snapshot.Layout.AutoMode;
        _autoError = null;
        await Execute(c => c.SetAutoMode(enable), enable ? "Automatik einschalten" : "Automatik ausschalten");
    }

    /// <summary>Ist die Maschine lange genug leer, den obersten Vorrats-Eintrag schicken. Fehler zeigen keinen Dialog.</summary>
    private async Task CheckAutoFeed()
    {
        var controller = _controller;
        var snap = _snapshot;
        if (controller == null || snap == null || _error != null)
        {
            _autoFeeder.Restart();
            ShowAutoStatus(false, new AutoFeedStatus(AutoFeedState.Off, TimeSpan.Zero));
            return;
        }

        var status = _autoFeeder.Observe(snap.Layout.AutoMode, snap.Scan.Machine.Count, snap.Scan.Stock.Count, DateTime.UtcNow);
        ShowAutoStatus(snap.Layout.AutoMode, status);
        if (status.State != AutoFeedState.Due || _busy || StockList.IsDragging || MachineList.IsDragging) return;

        _autoFeeder.Restart(); // Klappt es nicht, erst nach weiteren 60 s erneut versuchen.
        _busy = true;
        ListsArea.IsEnabled = false;
        BusyText.Text = "Automatik: nächster Auftrag zur Maschine …";
        OperationResult result;
        try
        {
            result = await Task.Run(controller.FeedNext);
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Automatik fehlgeschlagen", ex);
            result = OperationResult.Fail(ex.Message);
        }
        finally
        {
            BusyText.Text = "";
            ListsArea.IsEnabled = true;
            _busy = false;
        }
        _autoError = result.Success ? null : result.Message;
        await RefreshNow();
    }

    private void ShowAutoStatus(bool enabled, AutoFeedStatus status)
    {
        AutoStateText.Text = enabled ? "AN" : "AUS";
        AutoButton.Background = enabled ? (Brush)FindResource("RunningBg") : (Brush)FindResource("ButtonBg");
        if (enabled && _autoError != null)
        {
            AutoInfoText.Text = "Letzter Versuch fehlgeschlagen: " + _autoError;
            AutoInfoText.Foreground = (Brush)FindResource("Danger");
            return;
        }

        AutoInfoText.Foreground = (Brush)FindResource("FgDim");
        AutoInfoText.Text = status.State switch
        {
            AutoFeedState.MachineBusy => "Wartet, bis die Maschine leer ist",
            AutoFeedState.NoStock => "Maschine leer, Vorrat leer",
            AutoFeedState.Waiting => $"Maschine leer – nächster Auftrag in {Math.Ceiling(status.Remaining.TotalSeconds):0} s",
            AutoFeedState.Due => "Nächster Auftrag wird geschickt …",
            _ => "",
        };
    }

    private void Render()
    {
        var snap = _snapshot;
        if (_controller == null)
        {
            ShowEmpty("Bitte in den Einstellungen den Hauptordner wählen.");
            Status.Update([], "Kein Hauptordner gewählt");
            return;
        }
        if (_error != null || snap == null)
        {
            ShowEmpty(_error ?? "Wird geladen …");
            StatusText.Text = _error != null ? "⚠ " + _error : "Wird geladen …";
            StatusText.Foreground = _error != null ? (Brush)FindResource("Danger") : (Brush)FindResource("FgDim");
            Status.Update([], _error);
            return;
        }

        StockList.EmptyMessage = "Keine Aufträge im Vorrat";
        MachineList.EmptyMessage = "Keine Aufträge an der Maschine";
        StockList.SetRows(BuildRows(snap, Side.Stock));
        MachineList.SetRows(BuildRows(snap, Side.Machine));
        UpdateKanbanBar(snap.KanbanTiles);

        StockHeader.Text = $"Auftragsvorrat  ·  {Plural(snap.Scan.Stock.Count)}";
        MachineHeader.Text = $"An der Maschine  ·  {Plural(snap.Scan.Machine.Count)}";

        var running = snap.RunningKey != null ? $"   ·   Läuft: {snap.RunningKey}" : "";
        StatusText.Text = $"Stand {snap.Time:HH:mm:ss}   ·   wird alle {RefreshInterval.TotalSeconds:0} s aktualisiert{running}";
        StatusText.Foreground = (Brush)FindResource("FgDim");
        Status.Update(snap.Scan.Machine, null, Forecast(snap));
        UpdateDetails();
    }

    /// <summary>Wann welcher Auftrag an der Maschine dran ist, geschätzt aus den gemessenen Laufzeiten.</summary>
    private IReadOnlyList<ForecastEntry>? Forecast(QueueSnapshot snap)
    {
        if (_runTimes == null) return null;
        try
        {
            var records = _runTimes.Load(); // gleiche Liste, solange die Datei nicht wächst
            if (_estimator == null || !ReferenceEquals(records, _estimatorSource))
            {
                _estimator = new RunTimeEstimator(records);
                _estimatorSource = records;
            }
            return QueueForecast.Build(snap.Scan.Machine, _estimator, snap.RunningSinceUtc, DateTime.UtcNow);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string Plural(int n) => n == 1 ? "1 Platte" : $"{n} Platten";

    private void ShowEmpty(string message)
    {
        StockList.EmptyMessage = message;
        MachineList.EmptyMessage = message;
        StockList.SetRows([]);
        MachineList.SetRows([]);
        UpdateKanbanBar([]);
    }

    // ---------- Kanban-Kacheln ----------

    /// <summary>Kacheln in der Kopfzeile; nur neu aufbauen, wenn sich etwas geändert hat.</summary>
    private void UpdateKanbanBar(IReadOnlyList<Core.Kanban.KanbanTile> tiles)
    {
        var signature = string.Join("\n", tiles.Select(t => $"{t.Id}|{t.Name}|{t.Files.Count}"));
        if (signature == _kanbanSignature) return;
        _kanbanSignature = signature;

        KanbanBar.Children.Clear();
        foreach (var tile in tiles)
        {
            var content = new StackPanel();
            content.Children.Add(new TextBlock { Text = tile.Name, FontSize = 15, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center });
            content.Children.Add(new TextBlock
            {
                Text = Plural(tile.Files.Count),
                FontSize = 12,
                Foreground = (Brush)FindResource("FgDim"),
                HorizontalAlignment = HorizontalAlignment.Center,
            });
            var button = new Button
            {
                Content = content,
                MinWidth = 110,
                Padding = new Thickness(12, 3, 12, 3),
                Margin = new Thickness(3, 0, 3, 0),
                BorderBrush = (Brush)FindResource("Accent"),
                ToolTip = $"Kanban „{tile.Name}“: {Plural(tile.Files.Count)} als Kopie ans Ende der Maschine schicken\n" + string.Join("\n", tile.Files),
            };
            var id = tile.Id;
            var name = tile.Name;
            var count = tile.Files.Count;
            button.Click += async (_, _) => await SendKanban(id, name, count);
            KanbanBar.Children.Add(button);
        }
    }

    private async Task SendKanban(string tileId, string name, int count)
    {
        var answer = MessageBox.Show(this, $"Kanban „{name}“ ({Plural(count)}) als Kopie ans Ende der Maschine schicken?",
            "Kanban", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;
        await Execute(c => c.SendKanbanTile(tileId), $"Kanban „{name}“ zur Maschine");
    }

    private List<RowViewModel> BuildRows(QueueSnapshot snap, Side side)
    {
        var layout = snap.Layout.For(side);
        var jobs = snap.JobsOf(side);
        var pinned = side == Side.Machine ? snap.RunningKey : null;
        var rows = RowBuilder.Build(layout, pinned, _settings.CollapsedGroups);
        var result = new List<RowViewModel>(rows.Count);
        foreach (var row in rows)
        {
            if (row.Kind == RowKind.Group)
            {
                var members = row.Members.Select(k => jobs.GetValueOrDefault(k)).OfType<JobInfo>().ToList();
                result.Add(RowViewModel.ForGroup(row, layout.FindGroup(row.Id), members, _settings.CollapsedGroups.Contains(row.Id)));
            }
            else if (jobs.TryGetValue(row.Id, out var job))
            {
                result.Add(RowViewModel.ForJob(row, job));
            }
        }
        return result;
    }

    // ---------- Aktionen ----------

    private async Task Execute(Func<QueueController, OperationResult> op, string busyText)
    {
        var controller = _controller;
        if (controller == null || _busy) return;

        _busy = true;
        ListsArea.IsEnabled = false;
        BusyText.Text = busyText + " …";
        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            while (true)
            {
                var result = await Task.Run(() => op(controller));
                if (result.Success)
                {
                    if (result.Message != null)
                        MessageBox.Show(this, result.Message, "PConTRoL", MessageBoxButton.OK, MessageBoxImage.Information);
                    break;
                }

                Mouse.OverrideCursor = null;
                if (result.CanRetry)
                {
                    var answer = MessageBox.Show(this, result.Message + "\n\nErneut versuchen?", "Datei gesperrt",
                        MessageBoxButton.YesNo, MessageBoxImage.Warning);
                    if (answer != MessageBoxResult.Yes) break;
                    Mouse.OverrideCursor = Cursors.Wait;
                    continue;
                }

                MessageBox.Show(this, result.Message, "Nicht möglich", MessageBoxButton.OK, MessageBoxImage.Warning);
                break;
            }
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Aktion fehlgeschlagen", ex);
            MessageBox.Show(this, "Unerwarteter Fehler: " + ex.Message, "PConTRoL", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            Mouse.OverrideCursor = null;
            BusyText.Text = "";
            ListsArea.IsEnabled = true;
            _busy = false;
        }
        await RefreshNow();
    }

    private Task MoveSelection(JobListControl source, Side to, DropTarget target, string busyText)
    {
        var selection = source.GetSelection();
        if (selection.IsEmpty)
        {
            Hint(source.Side == Side.Machine && source.SelectedRows.Any(r => r.IsPinned)
                ? "Der laufende Auftrag bleibt an der Maschine."
                : "Bitte zuerst Aufträge markieren.");
            return Task.CompletedTask;
        }
        var from = source.Side;
        return Execute(c => c.Move(from, to, selection, target), busyText);
    }

    private void Hint(string text) => MessageBox.Show(this, text, "PConTRoL", MessageBoxButton.OK, MessageBoxImage.Information);

    private JobListControl ListOf(Side side) => side == Side.Stock ? StockList : MachineList;

    private async void OnSendEnd(object sender, RoutedEventArgs e) =>
        await MoveSelection(StockList, Side.Machine, DropTarget.End, "Zur Maschine");

    private async void OnReturn(object sender, RoutedEventArgs e) =>
        await MoveSelection(MachineList, Side.Stock, DropTarget.End, "Zurück in den Vorrat");

    private async void OnStep(object sender, RoutedEventArgs e)
    {
        var parts = ((string)((Button)sender).Tag).Split('|');
        var side = Enum.Parse<Side>(parts[0]);
        var step = Enum.Parse<MoveStep>(parts[1]);
        await StepSelection(side, step);
    }

    private async Task StepSelection(Side side, MoveStep step)
    {
        var selection = ListOf(side).GetSelection();
        if (selection.IsEmpty)
        {
            Hint("Bitte zuerst Aufträge markieren.");
            return;
        }
        await Execute(c => c.Step(side, selection, step), "Reihenfolge ändern");
    }

    private async void OnGroupButton(object sender, RoutedEventArgs e)
    {
        var side = Enum.Parse<Side>((string)((Button)sender).Tag);
        if (ListOf(side).GetSelection().IsEmpty) await CreateEmptyGroup(side);
        else await GroupSelection(side);
    }

    private async Task GroupSelection(Side side)
    {
        var list = ListOf(side);
        var selection = list.GetSelection();
        if (selection.IsEmpty) return;
        var suggestion = list.SelectedRows.FirstOrDefault(r => r.Job != null)?.Job?.NestJob ?? "Neue Gruppe";
        var name = InputDialog.Ask(this, "Gruppe bilden", "Name der Gruppe für die markierten Aufträge:", suggestion);
        if (name == null) return;
        await Execute(c => c.GroupSelection(side, selection, name), "Gruppe bilden");
    }

    private async Task CreateEmptyGroup(Side side)
    {
        var name = InputDialog.Ask(this, "Neue Gruppe", "Name der neuen Gruppe (danach Aufträge hineinziehen):", "Neue Gruppe");
        if (name == null) return;
        await Execute(c => c.CreateGroup(side, name), "Gruppe anlegen");
    }

    // ---------- IJobListHost ----------

    public async void OnDropRequested(JobListControl target, DragPayload payload, DropTarget drop)
    {
        var text = payload.Source == target.Side ? "Reihenfolge ändern"
            : target.Side == Side.Machine ? "Zur Maschine" : "Zurück in den Vorrat";
        await Execute(c => c.Move(payload.Source, target.Side, payload.Selection, drop), text);
    }

    public void OnToggleGroup(string groupId)
    {
        if (!_settings.CollapsedGroups.Remove(groupId)) _settings.CollapsedGroups.Add(groupId);
        SettingsStore.Save(_settings);
        Render();
    }

    public async void OnDeleteRequested(JobListControl list) => await DeleteSelection(list);

    private async void OnDeleteButton(object sender, RoutedEventArgs e) => await DeleteSelection(StockList);

    /// <summary>Markierte Aufträge (oder ganze Gruppen) nach Rückfrage in den Papierkorb im Hauptordner.</summary>
    private async Task DeleteSelection(JobListControl list)
    {
        var selection = list.GetSelection();
        if (selection.IsEmpty || _snapshot == null || _controller == null)
        {
            Hint(list.Side == Side.Machine && list.SelectedRows.Any(r => r.IsPinned)
                ? "Der laufende Auftrag kann nicht gelöscht werden."
                : "Bitte zuerst Aufträge markieren.");
            return;
        }

        var side = list.Side;
        var count = LayoutOps.ExpandKeys(_snapshot.Layout.For(side), selection, side == Side.Machine ? _snapshot.RunningKey : null).Count;
        if (count == 0) return;
        var what = count == 1 ? "den markierten Auftrag" : $"die {count} markierten Aufträge";
        var where = side == Side.Machine ? "\n\nSie verschwinden damit auch von der Maschine." : "";
        var answer = MessageBox.Show(this,
            $"Wirklich {what} löschen?{where}\n\nDie Dateien kommen in den Papierkorb\n{_controller.Paths.RecycleBin}\nund lassen sich von dort von Hand zurückholen.",
            "Löschen", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;
        await Execute(c => c.Delete(side, selection), "Löschen");
    }

    public void OnSelectionChanged(JobListControl list)
    {
        if (list.SelectedRows.Count > 0) _detailsSource = list;
        UpdateDetails();
    }

    public void OnBuildContextMenu(JobListControl list, ContextMenu menu)
    {
        var side = list.Side;
        var selection = list.GetSelection();
        var rows = list.SelectedRows;
        var has = !selection.IsEmpty;

        MenuItem Item(string header, Func<Task> action, bool enabled = true)
        {
            var item = new MenuItem { Header = header, IsEnabled = enabled };
            item.Click += async (_, _) => await action();
            menu.Items.Add(item);
            return item;
        }

        // An der Maschine ist die Reihenfolge fest: nur zurück in den Vorrat, Gruppen nur benennen/auflösen.
        if (side == Side.Stock)
        {
            Item("➜  An die Maschine (ans Ende)", () => MoveSelection(list, Side.Machine, DropTarget.End, "Zur Maschine"), has);
            Item("⤒  Ganz nach oben", () => StepSelection(side, MoveStep.Top), has);
            Item("⤓  Ganz nach unten", () => StepSelection(side, MoveStep.Bottom), has);
            Item("🗑  Löschen (Papierkorb) …", () => DeleteSelection(list), has);
            menu.Items.Add(new Separator());

            Item("Gruppe aus Auswahl bilden …", () => GroupSelection(side), has);

            var groups = _snapshot?.Layout.For(side).Groups ?? [];
            var moveTo = new MenuItem { Header = "In Gruppe verschieben", IsEnabled = has && groups.Count > 0 };
            foreach (var g in groups)
            {
                var target = new MenuItem { Header = g.Name };
                var id = g.Id;
                target.Click += async (_, _) =>
                    await Execute(c => c.Move(side, side, selection, new DropTarget(DropKind.IntoGroupEnd, id)), "In Gruppe verschieben");
                moveTo.Items.Add(target);
            }
            menu.Items.Add(moveTo);

            var groupedKeys = rows.Where(r => !r.IsGroup && !r.IsPinned && r.GroupId != null).Select(r => r.Id).ToList();
            Item("Aus Gruppe nehmen", () => Execute(c => c.RemoveFromGroup(side, groupedKeys), "Aus Gruppe nehmen"), groupedKeys.Count > 0);
        }
        else
        {
            Item("⬅  Zurück in den Vorrat", () => MoveSelection(list, Side.Stock, DropTarget.End, "Zurück in den Vorrat"), has);
            Item("🗑  Löschen (Papierkorb) …", () => DeleteSelection(list), has);
        }

        if (rows.Count == 1 && rows[0].IsGroup)
        {
            var groupId = rows[0].Id;
            var currentName = _snapshot?.Layout.For(side).FindGroup(groupId)?.Name ?? "";
            menu.Items.Add(new Separator());
            Item("Gruppe umbenennen …", async () =>
            {
                var name = InputDialog.Ask(this, "Gruppe umbenennen", "Neuer Name:", currentName);
                if (name != null) await Execute(c => c.RenameGroup(side, groupId, name), "Umbenennen");
            });
            Item("Gruppe auflösen (Aufträge bleiben)", () => Execute(c => c.DissolveGroup(side, groupId), "Gruppe auflösen"));
            Item(rows[0].IsCollapsed ? "Aufklappen" : "Zuklappen", () =>
            {
                OnToggleGroup(groupId);
                return Task.CompletedTask;
            });
        }

        // Nach Namen sortieren: Gruppenzeile oder Aufträge aus genau einer Gruppe markiert (nur im Vorrat).
        var sortGroups = rows.Select(r => r.IsGroup ? r.Id : r.GroupId).Distinct().ToList();
        if (side == Side.Stock && sortGroups is [{ } sortGroup])
        {
            menu.Items.Add(new Separator());
            Item("Gruppe nach Name sortieren (A–Z)", () => Execute(c => c.SortGroupByName(side, sortGroup), "Nach Name sortieren"));
        }

        menu.Items.Add(new Separator());
        if (side == Side.Stock) Item("Neue leere Gruppe …", () => CreateEmptyGroup(side));
        Item("Alle Gruppen zuklappen", () => SetAllCollapsed(side, true));
        Item("Alle Gruppen aufklappen", () => SetAllCollapsed(side, false));
    }

    private Task SetAllCollapsed(Side side, bool collapsed)
    {
        foreach (var g in _snapshot?.Layout.For(side).Groups ?? [])
        {
            if (collapsed) _settings.CollapsedGroups.Add(g.Id);
            else _settings.CollapsedGroups.Remove(g.Id);
        }
        SettingsStore.Save(_settings);
        Render();
        return Task.CompletedTask;
    }

    // ---------- Details ----------

    private void UpdateDetails()
    {
        var rows = _detailsSource?.SelectedRows ?? [];
        if (rows.Count != 1)
        {
            DetailTitle.Text = rows.Count > 1 ? $"{rows.Count} Zeilen markiert" : "Auftrag anklicken für Details";
            DetailText.Text = "";
            PartsGrid.ItemsSource = null;
            PreviewImage.Source = null;
            _previewPath = null;
            return;
        }

        var row = rows[0];
        if (row.IsGroup)
        {
            DetailTitle.Text = row.Name;
            DetailText.Text = $"Material: {row.Material}\nMaße: {row.Dims}\nTeile gesamt: {row.Parts}";
            ShowParts(row.Members.SelectMany(m => m.Parts));
            PreviewImage.Source = null;
            _previewPath = null;
            return;
        }

        var job = row.Job!;
        DetailTitle.Text = job.Key;
        var where = job.Side == Side.Machine
            ? (row.IsPinned ? "An der Maschine – läuft gerade" : $"An der Maschine – Position {row.PositionText}")
            : "Im Vorrat";
        var lines = new List<string>
        {
            where,
            $"Nest-Job: {job.NestJob}",
            $"Platte: {job.BoardText}",
            $"Material: {job.Material}",
            $"Maße: {job.DimsText}",
            $"Teile: {job.PartsOnBoard?.ToString() ?? "–"}" + (job.Efficiency is { } eff ? $"   ·   Ausnutzung {eff.ToString("0.0", De)} %" : ""),
        };
        if (job.Priority != null) lines.Add($"Priorität (BetterNest): {job.Priority}");
        if (job.ProductionNumber.Length > 0) lines.Add($"Prod.-Nr.: {job.ProductionNumber}");
        lines.Add($"Dateidatum: {job.LastWriteUtc.ToLocalTime():dd.MM.yyyy HH:mm:ss}");
        DetailText.Text = string.Join("\n", lines);
        ShowParts(job.Parts);
        ShowPreview(FindPreview(job));
    }

    private string? _previewPath;

    private string? FindPreview(JobInfo job)
    {
        var sameFolder = Path.ChangeExtension(job.HopPath, ".jpg");
        if (File.Exists(sameFolder)) return sameFolder;
        if (_controller == null) return null;
        var inStock = Path.Combine(_controller.Paths.NestJobFolder(job.NestJob), job.Key + ".jpg");
        return File.Exists(inStock) ? inStock : null;
    }

    private void ShowPreview(string? path)
    {
        if (path == _previewPath) return;
        _previewPath = path;
        if (path == null)
        {
            PreviewImage.Source = null;
            return;
        }
        try
        {
            // Bild in den Speicher laden, damit die Datei nicht gesperrt bleibt.
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var ms = new MemoryStream();
            fs.CopyTo(ms);
            ms.Position = 0;
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 900;
            image.StreamSource = ms;
            image.EndInit();
            image.Freeze();
            PreviewImage.Source = image;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or UnauthorizedAccessException)
        {
            PreviewImage.Source = null;
        }
    }

    // ---------- Fenster ----------

    private async void OnRefreshClick(object sender, RoutedEventArgs e) => await RefreshNow();

    private async void OnRefreshCommand(object sender, ExecutedRoutedEventArgs e) => await RefreshNow();

    private void OnFullScreenClick(object sender, RoutedEventArgs e) => ToggleFullScreen();

    private void OnFullScreen(object sender, ExecutedRoutedEventArgs e) => ToggleFullScreen();

    private void OnPopupClick(object sender, RoutedEventArgs e) => Popup.ShowAtCorner();

    private void ToggleFullScreen()
    {
        _fullScreen = !_fullScreen;
        if (_fullScreen)
        {
            _stateBeforeFullScreen = WindowState;
            WindowState = WindowState.Normal;
            WindowStyle = WindowStyle.None;
            WindowState = WindowState.Maximized;
        }
        else
        {
            WindowStyle = WindowStyle.SingleBorderWindow;
            WindowState = _stateBeforeFullScreen;
        }
    }

    private void OnSettingsClick(object sender, RoutedEventArgs e) => OpenSettings();

    public void OpenSettings()
    {
        CaptureLayout();
        var dlg = new SettingsWindow(_settings, Popup, ApplyScale, Updates, _controller);
        if (IsVisible) dlg.Owner = this;
        else dlg.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var saved = dlg.ShowDialog() == true;
        if (dlg.KanbanChanged) _ = RefreshNow(); // Kacheln wirken sofort, auch bei „Abbrechen“

        // Fenstergröße merken, auch bei „Abbrechen“.
        _settings.SettingsWidth = dlg.Result.SettingsWidth = dlg.LastSize.Width;
        _settings.SettingsHeight = dlg.Result.SettingsHeight = dlg.LastSize.Height;
        if (!saved)
        {
            SettingsStore.Save(_settings);
            return;
        }

        var rootChanged = !string.Equals(dlg.Result.MainFolder, _settings.MainFolder, StringComparison.OrdinalIgnoreCase);
        var autostartChanged = dlg.Result.StartWithWindows != _settings.StartWithWindows;
        var databaseChanged = dlg.Result.DatabaseProtected != _settings.DatabaseProtected;
        _settings = dlg.Result;
        if (autostartChanged) Autostart.Apply(_settings.StartWithWindows);
        SettingsStore.Save(_settings);
        Popup.ApplyLook(_settings);
        ApplyScale();
        ApplyPartColumns();
        UpdateDetails(); // Zusammenfassen hängt von den sichtbaren Spalten ab
        if (rootChanged) ApplyRoot();
        if (databaseChanged) Database?.Configure(_settings.DatabaseConnection);

        // „Jetzt installieren“ in den Einstellungen: erst speichern (oben), dann wie der Update-Hinweis.
        if (dlg.InstallUpdateRequested) UpdateRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Spalten der Teileliste nach Einstellungen → Teileliste (Reihenfolge, Name, sichtbar).</summary>
    private void ApplyPartColumns()
    {
        PartsGrid.Columns.Clear();
        _partColumnKeys.Clear();

        // Feste erste Spalte: Stückzahl gleicher Teile (nicht einstellbar, nicht verschiebbar).
        var right = new Style(typeof(TextBlock));
        right.Setters.Add(new Setter(TextBlock.TextAlignmentProperty, TextAlignment.Right));
        PartsGrid.Columns.Add(new DataGridTextColumn
        {
            Header = PartColumnCatalog.CountHeader,
            Binding = new System.Windows.Data.Binding(nameof(PartRow.Count)),
            Width = new DataGridLength(55),
            CanUserReorder = false,
            ElementStyle = right,
        });
        PartsGrid.FrozenColumnCount = 1;

        foreach (var setting in _settings.PartColumns.Where(c => c.Visible))
        {
            if (PartColumnCatalog.Find(setting.Key) is not { } def) continue;
            var column = new DataGridTextColumn
            {
                Header = setting.Header,
                Binding = new System.Windows.Data.Binding(def.Path),
                Width = def.Width is { } w ? new DataGridLength(w) : new DataGridLength(1, DataGridLengthUnitType.Star),
            };
            _partColumnKeys[column] = def.Key;
            PartsGrid.Columns.Add(column);
        }
    }

    /// <summary>Spalten unten am Kopf verschoben: neue Reihenfolge übernehmen (ausgeblendete bleiben an ihrem Platz).</summary>
    private void CapturePartColumnOrder()
    {
        // Die ST-Spalte bleibt immer vorne.
        var count = PartsGrid.Columns.FirstOrDefault(c => !_partColumnKeys.ContainsKey(c));
        if (count is { DisplayIndex: not 0 }) count.DisplayIndex = 0;

        var shown = PartsGrid.Columns.OrderBy(c => c.DisplayIndex)
            .Where(_partColumnKeys.ContainsKey)
            .Select(c => _partColumnKeys[c]);
        _settings.PartColumns = PartColumnCatalog.Reorder(_settings.PartColumns, shown);
        SettingsStore.Save(_settings);
    }

    /// <summary>Teileliste füllen: gleiche Teile (alle angezeigten Spalten gleich) als eine Zeile mit Stückzahl.</summary>
    private void ShowParts(IEnumerable<PartInfo> parts)
    {
        var visible = _settings.PartColumns.Where(c => c.Visible).Select(c => c.Key);
        PartsGrid.ItemsSource = PartColumnCatalog.Group(parts, visible);
    }

    /// <summary>Vergrößert/verkleinert die ganze Oberfläche (Einstellungen → Größe).</summary>
    public void ApplyScale(double? preview = null)
    {
        var scale = UserSettings.ClampScale(preview ?? _settings.MainScale);
        RootGrid.LayoutTransform = Math.Abs(scale - 1) < 0.001 ? Transform.Identity : new ScaleTransform(scale, scale);
    }

    private void OnStatisticsClick(object sender, RoutedEventArgs e)
    {
        if (_statistics == null)
        {
            MessageBox.Show(this, "Bitte zuerst in den Einstellungen den Hauptordner wählen. Die Statistik liegt dort im Ordner _PConTRoL.",
                "Statistik", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        new StatisticsWindow(_statistics, _settings.MainScale) { Owner = this }.Show();
    }

    public void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized)
            WindowState = _settings.WindowMaximized ? WindowState.Maximized : WindowState.Normal;
        Activate();
        _ = RefreshNow();
    }

    private void HideToTray()
    {
        CaptureLayout();
        SettingsStore.Save(_settings);
        Hide();
        HiddenToTray?.Invoke(this, EventArgs.Empty);
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (AllowClose)
        {
            CaptureLayout();
            return;
        }
        e.Cancel = true;
        HideToTray();
    }

    /// <summary>Spalten, Fenstergröße und Detailhöhe in die Benutzereinstellungen übernehmen.</summary>
    public void CaptureLayout()
    {
        _settings.Columns["Stock"] = StockList.GetColumnStates();
        _settings.Columns["Machine"] = MachineList.GetColumnStates();
        _settings.DetailsHeight = DetailsRow.ActualHeight > 0 ? DetailsRow.ActualHeight : _settings.DetailsHeight;
        if (!_fullScreen && WindowState != WindowState.Minimized)
        {
            _settings.WindowMaximized = WindowState == WindowState.Maximized;
            if (WindowState == WindowState.Normal)
            {
                _settings.WindowLeft = Left;
                _settings.WindowTop = Top;
                _settings.WindowWidth = Width;
                _settings.WindowHeight = Height;
            }
        }
    }

    private void RestorePlacement()
    {
        Width = _settings.WindowWidth;
        Height = _settings.WindowHeight;
        if (!double.IsNaN(_settings.WindowLeft) && !double.IsNaN(_settings.WindowTop) &&
            _settings.WindowLeft >= SystemParameters.VirtualScreenLeft &&
            _settings.WindowTop >= SystemParameters.VirtualScreenTop &&
            _settings.WindowLeft < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 100)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = _settings.WindowLeft;
            Top = _settings.WindowTop;
        }
        else
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
        if (_settings.WindowMaximized) WindowState = WindowState.Maximized;
        DetailsRow.Height = new GridLength(Math.Max(60, _settings.DetailsHeight));
    }

    protected override void OnClosed(EventArgs e)
    {
        _timer.Stop();
        foreach (var w in _watchers) w.Dispose();
        base.OnClosed(e);
    }
}
