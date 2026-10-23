using PConTRoL.Core.Files;
using PConTRoL.Core.Layout;

namespace PConTRoL.Core;

/// <summary>
/// Aufträge löschen. Endgültig gelöscht wird nichts: Alle Dateien eines Auftrags (.hop, .lbl, .jpg, .ups …) wandern
/// in den Papierkorb im Hauptordner (_PConTRoL\Papierkorb\&lt;Datum_Uhrzeit&gt;\…) und lassen sich von dort zurückholen.
/// Der laufende Auftrag an der Maschine wird nie gelöscht.
/// </summary>
public sealed partial class QueueController
{
    public OperationResult Delete(Side side, Selection selection)
    {
        lock (_sync)
        {
            return Guard(() =>
            {
                var snap = RefreshCore();
                var doc = _doc!;
                var running = side == Side.Machine ? snap.RunningKey : null;
                var requested = LayoutOps.ExpandKeys(doc.For(side), selection);
                var keys = requested.Where(k => !string.Equals(k, running, StringComparison.OrdinalIgnoreCase)).ToList();
                string? note = null;
                if (keys.Count < requested.Count)
                {
                    if (keys.Count == 0)
                        return OperationResult.Fail($"„{running}“ wird gerade an der Maschine bearbeitet und kann nicht gelöscht werden.");
                    note = $"„{running}“ wird gerade bearbeitet und wurde nicht gelöscht.";
                }
                if (keys.Count == 0) return OperationResult.NoChange;

                var bin = Path.Combine(_paths.RecycleBin, _utcNow().ToLocalTime().ToString("yyyy-MM-dd_HHmmss"));
                var jobs = snap.JobsOf(side);
                var moves = new List<(string From, string To)>();
                var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var key in keys)
                {
                    if (!jobs.TryGetValue(key, out var job)) continue;
                    var dir = Path.GetDirectoryName(job.HopPath)!;
                    folders.Add(dir);
                    var relative = Path.GetRelativePath(_paths.Root, dir);
                    // Nur Dateien genau dieses Auftrags (gleicher Name, beliebige Endung), .hop zuerst:
                    // An der Maschine ist der Auftrag damit sofort weg.
                    var files = Directory.EnumerateFiles(dir)
                        .Where(f => string.Equals(Path.GetFileNameWithoutExtension(f), key, StringComparison.OrdinalIgnoreCase))
                        .OrderBy(f => f.EndsWith(".hop", StringComparison.OrdinalIgnoreCase) ? 0 : 1);
                    foreach (var file in files)
                        moves.Add((file, Path.Combine(bin, relative, Path.GetFileName(file))));
                }

                var tx = new FileTransaction(_retry, _log);
                try
                {
                    foreach (var (from, to) in moves)
                        tx.Move(from, to, "In den Papierkorb");
                }
                catch (FileOperationException ex)
                {
                    return RollbackAndFail(tx, ex);
                }

                // Leer gewordene Job-Ordner im Vorrat aufräumen (nie _NestResult oder _Input selbst).
                foreach (var dir in folders.Where(d => side == Side.Stock && IsInside(d, _paths.NestResult)))
                    TryDeleteEmptyFolder(dir);

                if (side == Side.Machine) _finished.Forget(keys); // gelöscht ist nicht „fertig gefertigt“

                var layout = doc.For(side).Clone();
                LayoutOps.Extract(layout, selection, keys);
                if (side == Side.Stock) doc.Stock = layout;
                else doc.Machine = layout;
                _store.Save(doc);
                _log.Write("GELÖSCHT", "", $"{keys.Count} Aufträge in den Papierkorb: {bin}");
                return note == null ? OperationResult.Ok : new OperationResult(true, true, note);
            });
        }
    }
}
