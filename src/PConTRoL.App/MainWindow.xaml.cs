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
using PConTRoL.Core.Layout;
using PConTRoL.Core.Logging;

namespace PConTRoL.App;

public partial class MainWindow : Window, IJobListHost
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(3);
    private static readonly CultureInfo De = CultureInfo.GetCultureInfo("de-DE");

    private readonly DispatcherTimer _timer = new() { Interval = RefreshInterval };
    private readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly List<FileSystemWatcher> _watchers = [];
    private UserSettings _settings;
    private QueueController? _controller;
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

        ApplyRoot();
        _timer.Start();
    }

    public QueueStatus Status { get; }
    public PopupWindow Popup { get; }

    /// <summary>Wird von App beim Beenden gesetzt; sonst schließt das Fenster nur in den Tray.</summary>
    public bool AllowClose { get; set; }

    public UserSettings Settings => _settings;

    public event EventHandler? HiddenToTray;

    // ---------- Ordner / Aktualisierung ----------

    private void ApplyRoot()
    {
        foreach (var w in _watchers) w.Dispose();
        _watchers.Clear();
        _snapshot = null;

        var root = _settings.EffectiveRoot;
        TestBadge.Visibility = _settings.TestMode ? Visibility.Visible : Visibility.Collapsed;
        FolderText.Text = root.Length > 0 ? root : "Kein Hauptordner gewählt – bitte Einstellungen öffnen";
        Title = _settings.TestMode ? "PConTRoL – TESTMODUS" : "PConTRoL – Produktionsreihenfolge";

        StockList.ApplyColumnStates(_settings.Columns.GetValueOrDefault("Stock"));
        MachineList.ApplyColumnStates(_settings.Columns.GetValueOrDefault("Machine"));

        if (root.Length == 0)
        {
            _controller = null;
            Render();
            return;
        }

        var paths = new AppPaths(root);
        _controller = new QueueController(paths, new ActionLog(paths.LogFolder));
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
        if (controller == _controller) Render();
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

        StockHeader.Text = $"Auftragsvorrat  ·  {Plural(snap.Scan.Stock.Count)}";
        MachineHeader.Text = $"An der Maschine  ·  {Plural(snap.Scan.Machine.Count)}";

        var running = snap.RunningKey != null ? $"   ·   Läuft: {snap.RunningKey}" : "";
        StatusText.Text = $"Stand {snap.Time:HH:mm:ss}   ·   wird alle {RefreshInterval.TotalSeconds:0} s aktualisiert{running}";
        StatusText.Foreground = (Brush)FindResource("FgDim");
        Status.Update(snap.Scan.Machine, null);
        UpdateDetails();
    }

    private static string Plural(int n) => n == 1 ? "1 Platte" : $"{n} Platten";

    private void ShowEmpty(string message)
    {
        StockList.EmptyMessage = message;
        MachineList.EmptyMessage = message;
        StockList.SetRows([]);
        MachineList.SetRows([]);
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

    private async void OnSendNext(object sender, RoutedEventArgs e) =>
        await MoveSelection(StockList, Side.Machine, DropTarget.Start, "Zur Maschine");

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

        if (side == Side.Stock)
        {
            Item("➜  An die Maschine (ans Ende)", () => MoveSelection(list, Side.Machine, DropTarget.End, "Zur Maschine"), has);
            Item("⇥  An die Maschine (als Nächstes)", () => MoveSelection(list, Side.Machine, DropTarget.Start, "Zur Maschine"), has);
            Item("⤒  Ganz nach oben", () => StepSelection(side, MoveStep.Top), has);
        }
        else
        {
            Item("⇥  Als Nächstes", () => StepSelection(side, MoveStep.Top), has);
            Item("⬅  Zurück in den Vorrat", () => MoveSelection(list, Side.Stock, DropTarget.End, "Zurück in den Vorrat"), has);
        }
        Item("⤓  Ganz nach unten", () => StepSelection(side, MoveStep.Bottom), has);
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

        menu.Items.Add(new Separator());
        Item("Neue leere Gruppe …", () => CreateEmptyGroup(side));
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
            PartsGrid.ItemsSource = row.Members.SelectMany(m => m.Parts).ToList();
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
        PartsGrid.ItemsSource = job.Parts;
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
        var dlg = new SettingsWindow(_settings, Popup);
        if (IsVisible) dlg.Owner = this;
        else dlg.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        if (dlg.ShowDialog() != true) return;

        var rootChanged = !string.Equals(dlg.Result.EffectiveRoot, _settings.EffectiveRoot, StringComparison.OrdinalIgnoreCase)
                          || dlg.Result.TestMode != _settings.TestMode;
        var autostartChanged = dlg.Result.StartWithWindows != _settings.StartWithWindows;
        _settings = dlg.Result;
        if (autostartChanged) Autostart.Apply(_settings.StartWithWindows);
        SettingsStore.Save(_settings);
        Popup.ApplyLook(_settings);
        if (rootChanged) ApplyRoot();
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
