using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using PConTRoL.App.Services;
using PConTRoL.App.ViewModels;
using PConTRoL.Core;
using PConTRoL.Core.Layout;

namespace PConTRoL.App.Views;

/// <summary>Was per Drag &amp; Drop transportiert wird.</summary>
public sealed class DragPayload
{
    public required Side Source { get; init; }
    public required Selection Selection { get; init; }
}

/// <summary>Das Hauptfenster erledigt die eigentlichen Aktionen; die Liste meldet nur, was der Nutzer will.</summary>
public interface IJobListHost
{
    void OnDropRequested(JobListControl target, DragPayload payload, DropTarget drop);
    void OnToggleGroup(string groupId);
    void OnBuildContextMenu(JobListControl list, ContextMenu menu);
    void OnSelectionChanged(JobListControl list);
    void OnDeleteRequested(JobListControl list);
}

/// <summary>Eine der beiden Auftragslisten: mehrspaltig, Gruppen, Mehrfachauswahl, Drag &amp; Drop.</summary>
public partial class JobListControl : UserControl
{
    public const string DragFormat = "PConTRoL.DragPayload";

    private sealed record ColumnDef(string Key, string Header, string? Path, double Width, bool Stock, bool Machine, bool Right = false);

    private static readonly ColumnDef[] ColumnDefs =
    [
        new("name", "Auftrag", null, 380, true, true),
        new("job", "Nest-Job", nameof(RowViewModel.NestJob), 280, false, false),
        new("board", "Platte", nameof(RowViewModel.Board), 85, true, true, Right: true),
        new("material", "Material", nameof(RowViewModel.Material), 200, true, true),
        new("dims", "Maße", nameof(RowViewModel.Dims), 175, true, true),
        new("parts", "Teile", nameof(RowViewModel.Parts), 70, true, true, Right: true),
        new("eff", "Ausnutzung", nameof(RowViewModel.Efficiency), 115, false, false, Right: true),
        new("prio", "Prio", nameof(RowViewModel.Priority), 70, true, false, Right: true),
        new("prodnr", "Prod.-Nr.", nameof(RowViewModel.ProductionNumber), 120, false, false),
        new("modified", "Geändert", nameof(RowViewModel.Modified), 160, false, true),
    ];

    private readonly Dictionary<DataGridColumn, string> _columnKeys = [];
    private Point? _dragStart;
    private RowViewModel? _deferredSelect;
    private RowViewModel? _indicatorRow;
    private string _signature = "";

    public JobListControl()
    {
        InitializeComponent();
        Grid.ItemsSource = Rows;
        BuildColumns();

        Grid.PreviewMouseLeftButtonDown += OnPreviewMouseLeftButtonDown;
        Grid.PreviewMouseMove += OnPreviewMouseMove;
        Grid.PreviewMouseLeftButtonUp += OnPreviewMouseLeftButtonUp;
        Grid.MouseDoubleClick += OnMouseDoubleClick;
        Grid.DragOver += OnDragOver;
        Grid.DragLeave += (_, _) => SetIndicator(null, DropIndicator.None);
        Grid.Drop += OnDrop;
        Grid.SelectionChanged += (_, _) => Host?.OnSelectionChanged(this);
        Grid.ContextMenu = new ContextMenu();
        Grid.ContextMenuOpening += OnContextMenuOpening;
        Grid.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Delete || SelectedRows.Count == 0) return;
            Host?.OnDeleteRequested(this);
            e.Handled = true;
        };
    }

    public Side Side { get; set; }
    public IJobListHost? Host { get; set; }
    public ObservableCollection<RowViewModel> Rows { get; } = [];
    public bool IsDragging { get; private set; }

    public string EmptyMessage
    {
        get => EmptyText.Text;
        set => EmptyText.Text = value;
    }

    public IReadOnlyList<RowViewModel> SelectedRows => Grid.SelectedItems.Cast<RowViewModel>().ToList();

    /// <summary>Auswahl als Kern-Selection: Aufträge + ganze Gruppen. Der laufende Auftrag zählt nicht mit.</summary>
    public Selection GetSelection()
    {
        var rows = SelectedRows;
        var groups = rows.Where(r => r.IsGroup).Select(r => r.Id).ToHashSet();
        var keys = rows.Where(r => !r.IsGroup && !r.IsPinned && (r.GroupId == null || !groups.Contains(r.GroupId)))
            .Select(r => r.Id).ToList();
        return new Selection { Keys = keys, WholeGroups = groups };
    }

    /// <summary>Neue Zeilen übernehmen. Auswahl und Scrollposition bleiben erhalten.</summary>
    public void SetRows(IReadOnlyList<RowViewModel> rows)
    {
        EmptyText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        var signature = string.Join("\n", rows.Select(r => r.Signature));
        if (signature == _signature) return;
        _signature = signature;

        var selected = SelectedRows.Select(r => (r.IsGroup, r.Id)).ToHashSet();
        var scroll = FindDescendant<ScrollViewer>(Grid);
        var offset = scroll?.VerticalOffset ?? 0;

        Rows.Clear();
        foreach (var r in rows) Rows.Add(r);

        Grid.SelectedItems.Clear();
        foreach (var r in Rows.Where(r => selected.Contains((r.IsGroup, r.Id))))
            Grid.SelectedItems.Add(r);

        if (scroll != null)
            Dispatcher.BeginInvoke(() => scroll.ScrollToVerticalOffset(offset), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    public void SelectKeys(IEnumerable<string> keys)
    {
        var set = keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        Grid.SelectedItems.Clear();
        foreach (var r in Rows.Where(r => !r.IsGroup && set.Contains(r.Id)))
            Grid.SelectedItems.Add(r);
    }

    public void ForceRefresh() => _signature = "";

    // ---------- Spalten ----------

    private void BuildColumns()
    {
        foreach (var def in ColumnDefs)
        {
            DataGridColumn column;
            if (def.Path == null)
            {
                column = new DataGridTemplateColumn { CellTemplate = (DataTemplate)Resources["NameCell"] };
            }
            else
            {
                var text = new DataGridTextColumn { Binding = new Binding(def.Path) };
                if (def.Right)
                {
                    var style = new Style(typeof(TextBlock));
                    style.Setters.Add(new Setter(TextBlock.TextAlignmentProperty, TextAlignment.Right));
                    text.ElementStyle = style;
                }
                column = text;
            }
            column.Header = def.Header;
            column.Width = new DataGridLength(def.Width);
            _columnKeys[column] = def.Key;
            Grid.Columns.Add(column);
        }
    }

    public void ApplyColumnStates(List<ColumnState>? states)
    {
        foreach (var (column, key) in _columnKeys)
        {
            var def = ColumnDefs.First(d => d.Key == key);
            var state = states?.FirstOrDefault(s => s.Key == key);
            var visible = state?.Visible ?? (Side == Side.Stock ? def.Stock : def.Machine);
            column.Visibility = visible || key == "name" ? Visibility.Visible : Visibility.Collapsed;
            if (state is { Width: > 20 }) column.Width = new DataGridLength(state.Width);
        }

        if (states is { Count: > 0 })
        {
            var ordered = _columnKeys
                .OrderBy(kv => states.FirstOrDefault(s => s.Key == kv.Value)?.DisplayIndex ?? int.MaxValue)
                .Select(kv => kv.Key).ToList();
            for (var i = 0; i < ordered.Count; i++) ordered[i].DisplayIndex = i;
        }
    }

    public List<ColumnState> GetColumnStates() =>
        _columnKeys.Select(kv => new ColumnState
        {
            Key = kv.Value,
            Visible = kv.Key.Visibility == Visibility.Visible,
            Width = kv.Key.ActualWidth > 0 ? kv.Key.ActualWidth : kv.Key.Width.DisplayValue,
            DisplayIndex = kv.Key.DisplayIndex,
        }).ToList();

    private void FillColumnMenu(ContextMenu menu)
    {
        menu.Items.Add(new MenuItem { Header = "Spalten anzeigen:", IsEnabled = false });
        foreach (var (column, key) in _columnKeys.OrderBy(kv => kv.Key.DisplayIndex))
        {
            if (key == "name") continue;
            var item = new MenuItem
            {
                Header = column.Header,
                IsCheckable = true,
                IsChecked = column.Visibility == Visibility.Visible,
                StaysOpenOnClick = true,
            };
            item.Click += (_, _) => column.Visibility = item.IsChecked ? Visibility.Visible : Visibility.Collapsed;
            menu.Items.Add(item);
        }
    }

    // ---------- Maus / Drag & Drop ----------

    private void OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = null;
        _deferredSelect = null;
        var source = e.OriginalSource as DependencyObject;
        if (source == null || FindAncestor<DataGridColumnHeader>(source) != null || FindAncestor<ScrollBar>(source) != null)
            return;

        var row = FindAncestor<DataGridRow>(source);
        if (row?.DataContext is not RowViewModel vm) return;

        if (source is FrameworkElement { Tag: "toggle" } && vm.IsGroup)
        {
            Host?.OnToggleGroup(vm.Id);
            e.Handled = true;
            return;
        }

        _dragStart = e.GetPosition(Grid);

        // Klick auf eine bereits markierte Zeile: Auswahl erst beim Loslassen ändern, damit man mehrere ziehen kann.
        if (row.IsSelected && Keyboard.Modifiers == ModifierKeys.None && Grid.SelectedItems.Count > 1)
        {
            _deferredSelect = vm;
            e.Handled = true;
        }
    }

    private void OnPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_deferredSelect != null)
        {
            Grid.SelectedItems.Clear();
            Grid.SelectedItem = _deferredSelect;
        }
        _deferredSelect = null;
        _dragStart = null;
    }

    private void OnPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragStart is not { } start || e.LeftButton != MouseButtonState.Pressed) return;
        var pos = e.GetPosition(Grid);
        if (Math.Abs(pos.X - start.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(pos.Y - start.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        _dragStart = null;
        _deferredSelect = null;
        var selection = GetSelection();
        if (selection.IsEmpty) return;

        IsDragging = true;
        try
        {
            var data = new DataObject(DragFormat, new DragPayload { Source = Side, Selection = selection });
            DragDrop.DoDragDrop(Grid, data, DragDropEffects.Move);
        }
        finally
        {
            IsDragging = false;
            SetIndicator(null, DropIndicator.None);
        }
    }

    private void OnMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d && FindAncestor<DataGridRow>(d)?.DataContext is RowViewModel { IsGroup: true } vm)
            Host?.OnToggleGroup(vm.Id);
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DragFormat))
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        var (target, row, indicator) = ResolveDrop(e);
        SetIndicator(row, indicator);
        e.Effects = target != null ? DragDropEffects.Move : DragDropEffects.None;
        AutoScroll(e.GetPosition(Grid));
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        var (target, _, _) = ResolveDrop(e);
        SetIndicator(null, DropIndicator.None);
        if (target != null && e.Data.GetData(DragFormat) is DragPayload payload)
            Host?.OnDropRequested(this, payload, target);
        e.Handled = true;
    }

    /// <summary>Ermittelt aus der Mausposition, wohin eingefügt wird, und welche Markierung gezeigt wird.</summary>
    private (DropTarget? Target, RowViewModel? Row, DropIndicator Indicator) ResolveDrop(DragEventArgs e)
    {
        // Maschine: feste Reihenfolge – nichts umsortieren, Neues kommt immer ans Ende.
        if (Side == Side.Machine)
        {
            if (e.Data.GetData(DragFormat) is not DragPayload { Source: Side.Stock })
                return (null, null, DropIndicator.None);
            var last = Rows.LastOrDefault();
            return (DropTarget.End, last, last != null ? DropIndicator.Below : DropIndicator.None);
        }

        var pos = e.GetPosition(Grid);
        var hit = Grid.InputHitTest(pos) as DependencyObject;
        if (hit != null && FindAncestor<DataGridColumnHeader>(hit) != null)
            return (DropTarget.Start, null, DropIndicator.None);

        var rowElement = hit != null ? FindAncestor<DataGridRow>(hit) : null;
        if (rowElement?.DataContext is not RowViewModel vm)
        {
            var last = Rows.LastOrDefault();
            return (DropTarget.End, last, last != null ? DropIndicator.Below : DropIndicator.None);
        }

        var y = e.GetPosition(rowElement).Y / Math.Max(1, rowElement.ActualHeight);

        if (vm.IsPinned)
            return (new DropTarget(DropKind.AfterJob, vm.Id), vm, DropIndicator.Below);

        if (!vm.IsGroup)
            return y < 0.5
                ? (new DropTarget(DropKind.BeforeJob, vm.Id), vm, DropIndicator.Above)
                : (new DropTarget(DropKind.AfterJob, vm.Id), vm, DropIndicator.Below);

        if (y < 0.25) return (new DropTarget(DropKind.BeforeGroup, vm.Id), vm, DropIndicator.Above);
        if (y > 0.75)
            return vm.IsCollapsed || vm.Members.Count == 0
                ? (new DropTarget(DropKind.AfterGroup, vm.Id), vm, DropIndicator.Below)
                : (new DropTarget(DropKind.IntoGroupStart, vm.Id), vm, DropIndicator.Below);
        return (new DropTarget(DropKind.IntoGroupEnd, vm.Id), vm, DropIndicator.Into);
    }

    private void SetIndicator(RowViewModel? row, DropIndicator indicator)
    {
        if (_indicatorRow != null && _indicatorRow != row) _indicatorRow.Drop = DropIndicator.None;
        _indicatorRow = row;
        if (row != null) row.Drop = indicator;
    }

    private void AutoScroll(Point pos)
    {
        var scroll = FindDescendant<ScrollViewer>(Grid);
        if (scroll == null) return;
        if (pos.Y < 50) scroll.LineUp();
        else if (pos.Y > Grid.ActualHeight - 40) scroll.LineDown();
    }

    private void OnContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var menu = Grid.ContextMenu!;
        menu.Items.Clear();
        menu.FontSize = 16;

        if (e.OriginalSource is DependencyObject d && FindAncestor<DataGridColumnHeader>(d) != null)
            FillColumnMenu(menu);
        else
            Host?.OnBuildContextMenu(this, menu);

        if (menu.Items.Count == 0) e.Handled = true;
    }

    // ---------- Hilfen ----------

    private static T? FindAncestor<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d != null)
        {
            if (d is T t) return t;
            d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        }
        return null;
    }

    private static T? FindDescendant<T>(DependencyObject d) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
        {
            var child = VisualTreeHelper.GetChild(d, i);
            if (child is T t) return t;
            if (FindDescendant<T>(child) is { } found) return found;
        }
        return null;
    }
}
