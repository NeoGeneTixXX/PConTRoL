using PConTRoL.Core.Files;
using PConTRoL.Core.Layout;
using PConTRoL.Core.Logging;
using PConTRoL.Core.Ordering;
using PConTRoL.Core.Scanning;

namespace PConTRoL.Core;

public sealed record OperationResult(bool Success, bool Changed, string? Message = null, bool CanRetry = false)
{
    public static readonly OperationResult Ok = new(true, true);
    public static readonly OperationResult NoChange = new(true, false);

    public static OperationResult Fail(string message, bool canRetry = false) => new(false, false, message, canRetry);
}

/// <summary>Stand beider Seiten zu einem Zeitpunkt (für die Anzeige).</summary>
public sealed class QueueSnapshot
{
    public required ScanResult Scan { get; init; }
    public required LayoutDocument Layout { get; init; }
    public required DateTime Time { get; init; }

    public string? RunningKey => Scan.RunningKey;

    public IReadOnlyDictionary<string, JobInfo> StockByKey =>
        _stock ??= Scan.Stock.GroupBy(j => j.Key, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, JobInfo> MachineByKey =>
        _machine ??= Scan.Machine.GroupBy(j => j.Key, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, JobInfo> JobsOf(Side side) => side == Side.Stock ? StockByKey : MachineByKey;

    private Dictionary<string, JobInfo>? _stock;
    private Dictionary<string, JobInfo>? _machine;
}

/// <summary>
/// Zentrale Steuerung: liest die Ordner, hält Reihenfolge/Gruppen und führt Aktionen sicher aus.
/// Alle öffentlichen Methoden sind threadsicher (eine Aktion zur Zeit).
/// </summary>
public sealed class QueueController
{
    private readonly object _sync = new();
    private readonly AppPaths _paths;
    private readonly ActionLog _log;
    private readonly RetryPolicy _retry;
    private readonly Func<DateTime> _utcNow;
    private readonly MachineSortOrder _sortOrder;
    private readonly FolderScanner _scanner;
    private readonly LayoutStore _store;
    private LayoutDocument? _doc;

    public QueueController(AppPaths paths, ActionLog log, RetryPolicy? retry = null, Func<DateTime>? utcNow = null,
        MachineSortOrder sortOrder = MachineConfig.SortOrder)
    {
        _paths = paths;
        _log = log;
        _retry = retry ?? RetryPolicy.Default;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _sortOrder = sortOrder;
        _scanner = new FolderScanner(paths, sortOrder);
        _store = new LayoutStore(paths.LayoutFile);
    }

    public AppPaths Paths => _paths;

    public QueueSnapshot Refresh()
    {
        lock (_sync) return RefreshCore();
    }

    /// <summary>Drag &amp; Drop oder Pfeil-Buttons: innerhalb einer Seite oder auf die andere Seite.</summary>
    public OperationResult Move(Side from, Side to, Selection selection, DropTarget target)
    {
        lock (_sync)
        {
            return Guard(() =>
            {
                if (from == to)
                    return Mutate(from, (layout, pinned) => LayoutOps.Move(layout, selection, target, pinned), "Reihenfolge geändert");
                return from == Side.Stock ? SendToMachine(selection, target) : ReturnToStock(selection, target);
            });
        }
    }

    public OperationResult Step(Side side, Selection selection, MoveStep step)
    {
        lock (_sync)
            return Guard(() => Mutate(side, (layout, pinned) => LayoutOps.Step(layout, selection, step, pinned), "Priorität geändert"));
    }

    public OperationResult CreateGroup(Side side, string name)
    {
        lock (_sync)
            return Guard(() => Mutate(side, (layout, _) => LayoutOps.CreateEmptyGroup(layout, name) != null, "Gruppe angelegt"));
    }

    public OperationResult GroupSelection(Side side, Selection selection, string name)
    {
        lock (_sync)
            return Guard(() => Mutate(side, (layout, pinned) => LayoutOps.GroupSelection(layout, selection, name, pinned) != null, "Gruppe gebildet"));
    }

    public OperationResult RenameGroup(Side side, string groupId, string name)
    {
        lock (_sync)
            return Guard(() => Mutate(side, (layout, _) => LayoutOps.RenameGroup(layout, groupId, name), "Gruppe umbenannt"));
    }

    public OperationResult DissolveGroup(Side side, string groupId)
    {
        lock (_sync)
            return Guard(() => Mutate(side, (layout, _) => LayoutOps.DissolveGroup(layout, groupId), "Gruppe aufgelöst"));
    }

    public OperationResult RemoveFromGroup(Side side, IReadOnlyList<string> keys)
    {
        lock (_sync)
            return Guard(() => Mutate(side, (layout, pinned) => LayoutOps.RemoveFromGroup(layout, keys, pinned), "Aus Gruppe genommen"));
    }

    private QueueSnapshot RefreshCore()
    {
        if (!_paths.IsValid)
            throw new DirectoryNotFoundException(
                $"Hauptordner nicht gefunden oder ohne {AppPaths.InputFolderName}/{AppPaths.NestResultFolderName}: {_paths.Root}");

        var scan = _scanner.Scan();
        if (_doc == null || _store.HasChangedOnDisk())
            _doc = _store.Load();
        LayoutOps.Reconcile(_doc, scan);
        return new QueueSnapshot { Scan = scan, Layout = _doc.Clone(), Time = DateTime.Now };
    }

    private OperationResult Guard(Func<OperationResult> action)
    {
        try
        {
            return action();
        }
        catch (FileOperationException ex)
        {
            return OperationResult.Fail(ex.Message, ex.IsLocked);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Write("FEHLER", "", ex.Message);
            return OperationResult.Fail(ex.Message, canRetry: true);
        }
    }

    /// <summary>Layout-Änderung auf einer Seite. Auf der Maschinenseite werden danach die Daten angepasst.</summary>
    private OperationResult Mutate(Side side, Func<SideLayout, string?, bool> op, string reason)
    {
        var snap = RefreshCore();
        var doc = _doc!;
        var pinned = side == Side.Machine ? snap.RunningKey : null;
        var layout = doc.For(side).Clone();
        if (!op(layout, pinned)) return OperationResult.NoChange;

        if (side == Side.Machine && !layout.Order.SequenceEqual(doc.Machine.Order, StringComparer.OrdinalIgnoreCase))
        {
            var tx = new FileTransaction(_retry, _log);
            try
            {
                ApplyMachineOrder(layout.Order, snap, tx, reason);
            }
            catch (FileOperationException ex)
            {
                return RollbackAndFail(tx, ex);
            }
        }

        if (side == Side.Stock) doc.Stock = layout;
        else doc.Machine = layout;
        _store.Save(doc);
        return OperationResult.Ok;
    }

    private OperationResult SendToMachine(Selection selection, DropTarget target)
    {
        var snap = RefreshCore();
        var doc = _doc!;
        var keys = LayoutOps.ExpandKeys(doc.Stock, selection);
        if (keys.Count == 0) return OperationResult.NoChange;

        foreach (var key in keys)
        {
            if (snap.MachineByKey.ContainsKey(key) || File.Exists(Path.Combine(_paths.Input, key + ".hop")))
                return OperationResult.Fail($"„{key}“ liegt schon an der Maschine. Es wird nichts überschrieben.");
            if (File.Exists(Path.Combine(_paths.Input, key + ".lbl")))
                return OperationResult.Fail($"In _Input liegt schon eine Datei „{key}.lbl“. Es wird nichts überschrieben.");
        }

        var stock = doc.Stock.Clone();
        var machine = doc.Machine.Clone();
        var (keep, carried) = LayoutOps.Extract(stock, selection, keys);
        LayoutOps.Insert(machine, keys, keep, carried, target, snap.RunningKey);

        var tx = new FileTransaction(_retry, _log);
        try
        {
            // 1. Daten setzen – bei neuen Aufträgen noch im Vorrat, damit sie in _Input sofort richtig einsortiert sind.
            ApplyMachineOrder(machine.Order, snap, tx, "Zur Maschine");

            // 2. Verschieben: erst .lbl, dann .hop (die Maschine sieht die .hop erst, wenn alles da ist).
            foreach (var key in keys)
            {
                var job = snap.StockByKey[key];
                if (job.LblPath != null)
                    tx.Move(job.LblPath, Path.Combine(_paths.Input, Path.GetFileName(job.LblPath)), "Zur Maschine");
                tx.Move(job.HopPath, Path.Combine(_paths.Input, Path.GetFileName(job.HopPath)), "Zur Maschine");
            }
        }
        catch (FileOperationException ex)
        {
            return RollbackAndFail(tx, ex);
        }

        doc.Stock = stock;
        doc.Machine = machine;
        _store.Save(doc);
        return OperationResult.Ok;
    }

    private OperationResult ReturnToStock(Selection selection, DropTarget target)
    {
        var snap = RefreshCore();
        var doc = _doc!;
        var running = snap.RunningKey;
        var requested = LayoutOps.ExpandKeys(doc.Machine, selection);
        var keys = requested.Where(k => !string.Equals(k, running, StringComparison.OrdinalIgnoreCase)).ToList();
        string? note = null;
        if (keys.Count < requested.Count)
        {
            if (keys.Count == 0)
                return OperationResult.Fail($"„{running}“ wird gerade an der Maschine bearbeitet und bleibt dort.");
            note = $"„{running}“ wird gerade bearbeitet und wurde nicht verschoben.";
        }

        var moves = new List<(string From, string To)>();
        foreach (var key in keys)
        {
            var job = snap.MachineByKey[key];
            var folder = _paths.NestJobFolder(job.NestJob);
            var hopTarget = Path.Combine(folder, Path.GetFileName(job.HopPath));
            if (File.Exists(hopTarget))
                return OperationResult.Fail($"Im Vorrat liegt schon „{Path.GetFileName(hopTarget)}“. Es wird nichts überschrieben.");
            moves.Add((job.HopPath, hopTarget));

            if (job.LblPath != null)
            {
                var lblTarget = Path.Combine(folder, Path.GetFileName(job.LblPath));
                if (File.Exists(lblTarget))
                    return OperationResult.Fail($"Im Vorrat liegt schon „{Path.GetFileName(lblTarget)}“. Es wird nichts überschrieben.");
                moves.Add((job.LblPath, lblTarget));
            }

            // Vorschaubild, falls es (z. B. von Production Control) mit nach _Input gewandert ist.
            var jpg = Path.Combine(_paths.Input, key + ".jpg");
            var jpgTarget = Path.Combine(folder, key + ".jpg");
            if (File.Exists(jpg) && !File.Exists(jpgTarget))
                moves.Add((jpg, jpgTarget));
        }

        var stock = doc.Stock.Clone();
        var machine = doc.Machine.Clone();
        var (keep, carried) = LayoutOps.Extract(machine, selection, keys);
        if (target.Kind == DropKind.End && keep.Count == 0)
        {
            // "Zurück" ohne festes Ziel: wieder in die Gruppe seines Nest-Jobs, falls es die im Vorrat noch gibt.
            foreach (var batch in keys.GroupBy(k => snap.MachineByKey[k].NestJob, StringComparer.OrdinalIgnoreCase))
            {
                var groupId = LayoutOps.AutoGroupPrefix + batch.Key;
                var batchTarget = stock.FindGroup(groupId) != null ? new DropTarget(DropKind.IntoGroupEnd, groupId) : DropTarget.End;
                LayoutOps.Insert(stock, batch.ToList(), keep, carried, batchTarget, null);
            }
        }
        else
        {
            LayoutOps.Insert(stock, keys, keep, carried, target, null);
        }

        var tx = new FileTransaction(_retry, _log);
        try
        {
            // Erst .hop raus (Maschine sieht den Auftrag nicht mehr), dann der Rest.
            foreach (var (from, to) in moves)
                tx.Move(from, to, "Zurück in Vorrat");
        }
        catch (FileOperationException ex)
        {
            return RollbackAndFail(tx, ex);
        }

        doc.Stock = stock;
        doc.Machine = machine;
        _store.Save(doc);
        return note == null ? OperationResult.Ok : new OperationResult(true, true, note);
    }

    /// <summary>Setzt die Änderungsdaten so, dass die Maschine genau diese Reihenfolge abarbeitet.</summary>
    private void ApplyMachineOrder(IReadOnlyList<string> order, QueueSnapshot snap, FileTransaction tx, string reason)
    {
        var running = snap.RunningKey;
        if (running != null && (order.Count == 0 || !string.Equals(order[0], running, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Interner Fehler: Der laufende Auftrag muss oben bleiben.");

        JobInfo Find(string key) =>
            snap.MachineByKey.TryGetValue(key, out var m) ? m
            : snap.StockByKey.TryGetValue(key, out var s) ? s
            : throw new FileOperationException($"Auftrag „{key}“ nicht mehr vorhanden.", isLocked: false);

        var jobs = order.Select(Find).ToList();
        var plan = TimestampPlanner.Plan(
            jobs.Select(j => (j.Key, j.LastWriteUtc)).ToList(),
            fixedFirst: running != null,
            _utcNow(),
            _sortOrder);

        var byKey = jobs.ToDictionary(j => j.Key, StringComparer.OrdinalIgnoreCase);
        foreach (var change in plan)
        {
            var job = byKey[change.Key];
            if (string.Equals(job.Key, running, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Interner Fehler: Datum des laufenden Auftrags würde geändert.");
            tx.SetLastWriteTimeUtc(job.HopPath, change.NewUtc, reason);
            if (job.LblPath != null && File.Exists(job.LblPath))
                tx.SetLastWriteTimeUtc(job.LblPath, change.NewUtc, reason);
        }
    }

    private OperationResult RollbackAndFail(FileTransaction tx, FileOperationException ex)
    {
        var failed = tx.Rollback();
        _log.Write("ABGEBROCHEN", "", ex.Message);
        if (failed.Count == 0)
            return OperationResult.Fail(ex.Message + "\n\nEs wurde nichts verändert.", ex.IsLocked);
        return OperationResult.Fail(ex.Message + "\n\nACHTUNG – folgende Schritte konnten nicht rückgängig gemacht werden:\n"
                                    + string.Join("\n", failed), ex.IsLocked);
    }
}
