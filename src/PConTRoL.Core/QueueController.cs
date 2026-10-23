using PConTRoL.Core.Files;
using PConTRoL.Core.Layout;
using PConTRoL.Core.Logging;
using PConTRoL.Core.Ordering;
using PConTRoL.Core.Scanning;
using PConTRoL.Core.Statistics;

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
    private readonly StatisticsStore? _statistics;
    private readonly FinishedJobTracker _finished = new();
    private LayoutDocument? _doc;

    public QueueController(AppPaths paths, ActionLog log, RetryPolicy? retry = null, Func<DateTime>? utcNow = null,
        MachineSortOrder sortOrder = MachineConfig.SortOrder, StatisticsStore? statistics = null)
    {
        _statistics = statistics;
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

    /// <summary>
    /// An der Maschine ist die Reihenfolge fest: Aufträge kommen immer ans Ende, in der Reihenfolge,
    /// in der sie zur Maschine geschickt werden, und lassen sich dort nicht mehr umsortieren.
    /// </summary>
    public const string MachineOrderFixedMessage =
        "Die Reihenfolge an der Maschine ist fest: Aufträge werden in der Reihenfolge abgearbeitet, in der sie zur Maschine geschickt wurden.";

    /// <summary>Drag &amp; Drop oder Pfeil-Buttons: innerhalb einer Seite oder auf die andere Seite.</summary>
    public OperationResult Move(Side from, Side to, Selection selection, DropTarget target)
    {
        lock (_sync)
        {
            return Guard(() =>
            {
                if (from == to)
                    return Mutate(from, (layout, pinned) => LayoutOps.Move(layout, selection, target, pinned));
                return from == Side.Stock ? SendToMachine(selection) : ReturnToStock(selection, target);
            });
        }
    }

    public OperationResult Step(Side side, Selection selection, MoveStep step)
    {
        lock (_sync)
            return Guard(() => Mutate(side, (layout, pinned) => LayoutOps.Step(layout, selection, step, pinned)));
    }

    /// <summary>Automatik-Modus ein- oder ausschalten (gemeinsam für alle Rechner).</summary>
    public OperationResult SetAutoMode(bool enabled)
    {
        lock (_sync)
        {
            return Guard(() =>
            {
                RefreshCore();
                var doc = _doc!;
                if (doc.AutoMode == enabled) return OperationResult.NoChange;
                doc.AutoMode = enabled;
                _store.Save(doc);
                _log.Write("AUTOMATIK", "", enabled ? "eingeschaltet" : "ausgeschaltet");
                return OperationResult.Ok;
            });
        }
    }

    /// <summary>
    /// Automatik: schickt den obersten Eintrag des Vorrats (Einzelauftrag oder ganze Gruppe) zur Maschine –
    /// aber nur, wenn die Automatik an und die Maschine wirklich leer ist. Eine Sperrdatei im gemeinsamen
    /// Ordner verhindert, dass zwei Rechner gleichzeitig nachfüllen.
    /// </summary>
    public OperationResult FeedNext()
    {
        lock (_sync)
        {
            return Guard(() =>
            {
                FileStream? autoLock;
                try
                {
                    Directory.CreateDirectory(_paths.Shared);
                    autoLock = new FileStream(_paths.AutoLockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                }
                catch (IOException)
                {
                    return OperationResult.NoChange; // Ein anderer Rechner füllt gerade nach.
                }

                using (autoLock)
                {
                    var snap = RefreshCore();
                    var doc = _doc!;
                    if (!doc.AutoMode || snap.Scan.Machine.Count > 0) return OperationResult.NoChange;

                    var first = doc.Stock.Order.FirstOrDefault();
                    if (first == null) return OperationResult.NoChange;
                    var group = doc.Stock.GroupOf(first);
                    var selection = group != null ? Selection.OfGroup(group) : Selection.OfKeys(first);
                    var name = group != null ? doc.Stock.FindGroup(group)?.Name ?? first : first;

                    var result = SendToMachine(selection);
                    _log.Write("AUTOMATIK", name, result.Success ? "zur Maschine geschickt" : "fehlgeschlagen: " + result.Message);
                    return result;
                }
            });
        }
    }

    public OperationResult CreateGroup(Side side, string name)
    {
        lock (_sync)
            return Guard(() => Mutate(side, (layout, _) => LayoutOps.CreateEmptyGroup(layout, name) != null));
    }

    public OperationResult GroupSelection(Side side, Selection selection, string name)
    {
        lock (_sync)
            return Guard(() => Mutate(side, (layout, pinned) => LayoutOps.GroupSelection(layout, selection, name, pinned) != null));
    }

    public OperationResult RenameGroup(Side side, string groupId, string name)
    {
        lock (_sync)
            return Guard(() => Mutate(side, (layout, _) => LayoutOps.RenameGroup(layout, groupId, name)));
    }

    public OperationResult SortGroupByName(Side side, string groupId)
    {
        lock (_sync)
            return Guard(() => Mutate(side, (layout, pinned) => LayoutOps.SortGroupByName(layout, groupId, pinned)));
    }

    public OperationResult DissolveGroup(Side side, string groupId)
    {
        lock (_sync)
            return Guard(() => Mutate(side, (layout, _) => LayoutOps.DissolveGroup(layout, groupId)));
    }

    public OperationResult RemoveFromGroup(Side side, IReadOnlyList<string> keys)
    {
        lock (_sync)
            return Guard(() => Mutate(side, (layout, pinned) => LayoutOps.RemoveFromGroup(layout, keys, pinned)));
    }

    private QueueSnapshot RefreshCore()
    {
        if (!_paths.IsValid)
            throw new DirectoryNotFoundException(
                $"Hauptordner nicht gefunden oder ohne {AppPaths.InputFolderName}/{AppPaths.NestResultFolderName}: {_paths.Root}");

        var scan = _scanner.Scan();
        RecordFinished(scan);
        if (_doc == null || _store.HasChangedOnDisk())
            _doc = _store.Load();
        LayoutOps.Reconcile(_doc, scan);
        return new QueueSnapshot { Scan = scan, Layout = _doc.Clone(), Time = DateTime.Now };
    }

    /// <summary>Von der Maschine fertig bearbeitete Platten in die Statistik schreiben. Fehler stören den Betrieb nie.</summary>
    private void RecordFinished(ScanResult scan)
    {
        var finished = _finished.Update(scan);
        if (_statistics == null) return;
        foreach (var job in finished)
        {
            var record = BoardRecord.FromJob(job, BoardRecord.SourceMachine, _utcNow());
            if (record == null) continue;
            try
            {
                if (_statistics.Add(record))
                    _log.Write("FERTIG", job.Key, $"Statistik: {record.Material}, Ausnutzung {record.Utilization:0.0} %");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log.Write("FEHLER", job.Key, "Statistik nicht gespeichert: " + ex.Message);
            }
        }
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

    /// <summary>Layout-Änderung auf einer Seite. Auf der Maschinenseite darf sich nur die Gruppierung ändern, nie die Reihenfolge.</summary>
    private OperationResult Mutate(Side side, Func<SideLayout, string?, bool> op)
    {
        var snap = RefreshCore();
        var doc = _doc!;
        var pinned = side == Side.Machine ? snap.RunningKey : null;
        var layout = doc.For(side).Clone();
        if (!op(layout, pinned)) return OperationResult.NoChange;

        if (side == Side.Machine && !layout.Order.SequenceEqual(doc.Machine.Order, StringComparer.OrdinalIgnoreCase))
            return OperationResult.Fail(MachineOrderFixedMessage);

        if (side == Side.Stock) doc.Stock = layout;
        else doc.Machine = layout;
        _store.Save(doc);
        return OperationResult.Ok;
    }

    /// <summary>Neue Aufträge kommen immer ans Ende der Maschine, in Vorrats-Reihenfolge.</summary>
    private OperationResult SendToMachine(Selection selection)
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
        LayoutOps.Insert(machine, keys, keep, carried, DropTarget.End, snap.RunningKey);

        var tx = new FileTransaction(_retry, _log);
        try
        {
            // 1. Daten setzen – bei neuen Aufträgen noch im Vorrat, damit sie in _Input sofort ganz unten einsortiert sind.
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
