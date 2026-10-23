using PConTRoL.Core.Files;
using PConTRoL.Core.Kanban;
using PConTRoL.Core.Layout;

namespace PConTRoL.Core;

/// <summary>
/// Kanban-Kacheln: feste Zusammenstellungen von Platten, die per Klick als Gruppe an die Maschine gehen.
/// Beim Anlegen werden die Dateien in den Ordner Kanban\&lt;Kachel&gt;\ VERSCHOBEN (damit sie niemand versehentlich
/// wegräumt). Beim Klick gehen KOPIEN an die Maschine – die Kachel ist beliebig oft nutzbar.
/// </summary>
public sealed partial class QueueController
{
    private KanbanStore? _kanban;

    private KanbanStore KanbanStoreInstance => _kanban ??= new KanbanStore(_paths.KanbanFile);

    private IReadOnlyList<KanbanTile> LoadKanbanTiles()
    {
        try
        {
            return KanbanStoreInstance.Load().Tiles;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return []; // Kacheln nicht lesbar – die Auftragslisten funktionieren trotzdem.
        }
    }

    public IReadOnlyList<KanbanTile> KanbanTiles()
    {
        lock (_sync) return LoadKanbanTiles();
    }

    /// <summary>Neue Kachel: die gewählten .hop (mit .lbl und .jpg daneben) werden nach Kanban\&lt;Name&gt;\ verschoben.</summary>
    public OperationResult CreateKanbanTile(string name, IReadOnlyList<string> hopPaths)
    {
        lock (_sync)
        {
            return Guard(() =>
            {
                name = name.Trim();
                if (name.Length == 0) return OperationResult.Fail("Bitte einen Namen für die Kachel eingeben.");
                if (hopPaths.Count == 0) return OperationResult.Fail("Bitte mindestens eine .hop-Datei auswählen.");

                var doc = KanbanStoreInstance.Load();
                if (doc.Tiles.Any(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)))
                    return OperationResult.Fail($"Es gibt schon eine Kachel „{name}“.");

                var tile = new KanbanTile { Id = "k" + Guid.NewGuid().ToString("N")[..10], Name = name, Folder = UniqueKanbanFolder(name) };
                var result = ImportIntoTile(tile, hopPaths, "Kanban anlegen");
                if (!result.Success) return result;

                doc.Tiles.Add(tile);
                KanbanStoreInstance.Save(doc);
                _log.Write("KANBAN", name, $"Kachel angelegt mit {tile.Files.Count} Platten");
                return OperationResult.Ok;
            });
        }
    }

    /// <summary>Weitere Platten in eine Kachel aufnehmen (werden ebenfalls in ihren Ordner verschoben).</summary>
    public OperationResult AddToKanbanTile(string tileId, IReadOnlyList<string> hopPaths)
    {
        lock (_sync)
        {
            return Guard(() =>
            {
                var doc = KanbanStoreInstance.Load();
                var tile = doc.Tiles.FirstOrDefault(t => t.Id == tileId);
                if (tile == null) return OperationResult.Fail("Die Kachel gibt es nicht mehr.");
                if (hopPaths.Count == 0) return OperationResult.NoChange;

                var result = ImportIntoTile(tile, hopPaths, "Kanban ergänzen");
                if (!result.Success) return result;

                KanbanStoreInstance.Save(doc);
                _log.Write("KANBAN", tile.Name, $"{hopPaths.Count} Platten ergänzt");
                return OperationResult.Ok;
            });
        }
    }

    public OperationResult RenameKanbanTile(string tileId, string name)
    {
        lock (_sync)
        {
            return Guard(() =>
            {
                name = name.Trim();
                if (name.Length == 0) return OperationResult.Fail("Bitte einen Namen für die Kachel eingeben.");
                var doc = KanbanStoreInstance.Load();
                var tile = doc.Tiles.FirstOrDefault(t => t.Id == tileId);
                if (tile == null) return OperationResult.Fail("Die Kachel gibt es nicht mehr.");
                if (tile.Name == name) return OperationResult.NoChange;
                if (doc.Tiles.Any(t => t.Id != tileId && string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)))
                    return OperationResult.Fail($"Es gibt schon eine Kachel „{name}“.");

                _log.Write("KANBAN", tile.Name, $"umbenannt in „{name}“");
                tile.Name = name;
                KanbanStoreInstance.Save(doc);
                return OperationResult.Ok;
            });
        }
    }

    /// <summary>
    /// Kachel auflösen. Gelöscht wird nichts: Die Platten kommen als Nest-Job in den Vorrat (_NestResult\&lt;Ordner&gt;\).
    /// </summary>
    public OperationResult RemoveKanbanTile(string tileId)
    {
        lock (_sync)
        {
            return Guard(() =>
            {
                var doc = KanbanStoreInstance.Load();
                var tile = doc.Tiles.FirstOrDefault(t => t.Id == tileId);
                if (tile == null) return OperationResult.NoChange;

                var source = Path.Combine(_paths.Kanban, tile.Folder);
                var target = Path.Combine(_paths.NestResult, tile.Folder);
                var moves = Directory.Exists(source)
                    ? Directory.EnumerateFiles(source).Select(f => (From: f, To: Path.Combine(target, Path.GetFileName(f)))).ToList()
                    : [];
                foreach (var (_, to) in moves)
                    if (File.Exists(to))
                        return OperationResult.Fail($"Im Vorrat liegt schon „{Path.GetFileName(to)}“. Es wird nichts überschrieben.");

                var tx = new FileTransaction(_retry, _log);
                try
                {
                    // .hop zuletzt, damit der Vorrat sie erst sieht, wenn .lbl und .jpg schon da sind.
                    foreach (var (from, to) in moves.OrderBy(m => m.From.EndsWith(".hop", StringComparison.OrdinalIgnoreCase)))
                        tx.Move(from, to, "Kanban auflösen");
                }
                catch (FileOperationException ex)
                {
                    return RollbackAndFail(tx, ex);
                }

                TryDeleteEmptyFolder(source);
                doc.Tiles.Remove(tile);
                KanbanStoreInstance.Save(doc);
                _log.Write("KANBAN", tile.Name, $"Kachel aufgelöst, {moves.Count} Dateien in den Vorrat ({target})");
                return OperationResult.Ok;
            });
        }
    }

    /// <summary>
    /// Kachel geklickt: KOPIEN aller Platten gehen als Gruppe (Name = Kachelname) ans Ende der Maschine.
    /// Ablauf wie beim normalen Schicken: Production ID und Datum werden an der Kopie gesetzt, bevor sie in _Input auftaucht,
    /// erst .lbl, dann .hop. Die Originale im Kanban-Ordner bleiben unverändert.
    /// </summary>
    public OperationResult SendKanbanTile(string tileId)
    {
        lock (_sync)
        {
            return Guard(() =>
            {
                var tile = KanbanStoreInstance.Load().Tiles.FirstOrDefault(t => t.Id == tileId);
                if (tile == null) return OperationResult.Fail("Die Kachel gibt es nicht mehr.");
                if (tile.Files.Count == 0) return OperationResult.Fail($"Die Kachel „{tile.Name}“ enthält keine Platten.");

                var snap = RefreshCore();
                var doc = _doc!;
                var folder = Path.Combine(_paths.Kanban, tile.Folder);
                var sources = new List<(string Key, string Hop, string? Lbl)>();
                foreach (var file in tile.Files)
                {
                    var hop = Path.Combine(folder, file);
                    if (!File.Exists(hop))
                        return OperationResult.Fail($"„{file}“ fehlt im Kanban-Ordner ({folder}). Es wurde nichts geschickt.");
                    var key = Path.GetFileNameWithoutExtension(file);
                    if (snap.MachineByKey.ContainsKey(key) || File.Exists(Path.Combine(_paths.Input, key + ".hop"))
                        || File.Exists(Path.Combine(_paths.Input, key + ".lbl")))
                        return OperationResult.Fail($"„{key}“ liegt noch an der Maschine (noch nicht gefertigt). Es wurde nichts geschickt.");
                    var lbl = Path.ChangeExtension(hop, ".lbl");
                    sources.Add((key, hop, File.Exists(lbl) ? lbl : null));
                }

                var staging = Path.Combine(_paths.KanbanStaging, Guid.NewGuid().ToString("N")[..10]);
                var nextId = LastUsedProductionId(snap, doc);
                var tx = new FileTransaction(_retry, _log);
                try
                {
                    // 1. Kopien in die Zwischenablage (gleiches Laufwerk wie _Input) und dort vorbereiten.
                    var staged = new List<(string Key, string HopPath, string? LblPath, DateTime LastWriteUtc)>();
                    foreach (var (key, hop, lbl) in sources)
                    {
                        var stagedHop = Path.Combine(staging, Path.GetFileName(hop));
                        tx.Copy(hop, stagedHop, "Kanban");
                        string? stagedLbl = null;
                        if (lbl != null)
                        {
                            stagedLbl = Path.Combine(staging, Path.GetFileName(lbl));
                            tx.Copy(lbl, stagedLbl, "Kanban");
                        }
                        tx.SetProductionId(stagedHop, ++nextId, null, "Kanban");
                        staged.Add((key, stagedHop, stagedLbl, File.GetLastWriteTimeUtc(stagedHop)));
                    }
                    ApplyAppendDates(staged, snap, tx, "Kanban");

                    // 2. An die Maschine: erst .lbl, dann .hop.
                    foreach (var job in staged)
                    {
                        if (job.LblPath != null)
                            tx.Move(job.LblPath, Path.Combine(_paths.Input, Path.GetFileName(job.LblPath)), "Kanban");
                        tx.Move(job.HopPath, Path.Combine(_paths.Input, Path.GetFileName(job.HopPath)), "Kanban");
                    }
                }
                catch (FileOperationException ex)
                {
                    var failed = RollbackAndFail(tx, ex);
                    TryDeleteEmptyFolder(staging);
                    return failed;
                }
                TryDeleteEmptyFolder(staging);

                // Als Gruppe mit dem Kachelnamen ans Ende der Maschinenliste.
                var keys = sources.Select(s => s.Key).ToList();
                var group = new GroupInfo { Id = "kanban:" + Guid.NewGuid().ToString("N")[..10], Name = tile.Name, HadMembers = true };
                var machine = doc.Machine.Clone();
                LayoutOps.Insert(machine, keys, keys.ToDictionary(k => k, _ => group.Id, StringComparer.OrdinalIgnoreCase),
                    [group], DropTarget.End, snap.RunningKey);
                doc.Machine = machine;
                doc.LastProductionId = nextId;
                _store.Save(doc);
                _log.Write("KANBAN", tile.Name, $"{keys.Count} Platten als Kopie zur Maschine geschickt");
                return OperationResult.Ok;
            });
        }
    }

    /// <summary>Verschiebt .hop (+ .lbl, .jpg daneben) in den Ordner der Kachel und hängt sie an deren Liste an.</summary>
    private OperationResult ImportIntoTile(KanbanTile tile, IReadOnlyList<string> hopPaths, string reason)
    {
        var folder = Path.Combine(_paths.Kanban, tile.Folder);
        var names = new HashSet<string>(tile.Files, StringComparer.OrdinalIgnoreCase);
        var moves = new List<(string From, string To)>();
        var added = new List<string>();
        foreach (var raw in hopPaths)
        {
            var hop = Path.GetFullPath(raw);
            var file = Path.GetFileName(hop);
            if (!hop.EndsWith(".hop", StringComparison.OrdinalIgnoreCase) || !File.Exists(hop))
                return OperationResult.Fail($"„{file}“ ist keine vorhandene .hop-Datei.");
            if (IsInside(hop, _paths.Input))
                return OperationResult.Fail($"„{file}“ liegt an der Maschine. Bitte zuerst in den Vorrat zurückholen.");
            if (IsInside(hop, _paths.Kanban))
                return OperationResult.Fail($"„{file}“ gehört schon zu einer Kanban-Kachel.");
            if (!names.Add(file))
                return OperationResult.Fail($"„{file}“ ist doppelt – in einer Kachel muss jeder Dateiname eindeutig sein.");

            foreach (var path in new[] { hop, Path.ChangeExtension(hop, ".lbl"), Path.ChangeExtension(hop, ".jpg") })
            {
                if (!File.Exists(path)) continue;
                var to = Path.Combine(folder, Path.GetFileName(path));
                if (File.Exists(to))
                    return OperationResult.Fail($"Im Kanban-Ordner liegt schon „{Path.GetFileName(to)}“. Es wird nichts überschrieben.");
                moves.Add((path, to));
            }
            added.Add(file);
        }

        var tx = new FileTransaction(_retry, _log);
        try
        {
            foreach (var (from, to) in moves)
                tx.Move(from, to, reason);
        }
        catch (FileOperationException ex)
        {
            return RollbackAndFail(tx, ex);
        }
        tile.Files.AddRange(added);
        return OperationResult.Ok;
    }

    private string UniqueKanbanFolder(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var baseName = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        if (baseName.Length == 0) baseName = "Kachel";
        var candidate = baseName;
        for (var i = 2; Directory.Exists(Path.Combine(_paths.Kanban, candidate)); i++)
            candidate = $"{baseName} ({i})";
        return candidate;
    }

    private static bool IsInside(string path, string folder)
    {
        var f = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(f, StringComparison.OrdinalIgnoreCase);
    }

    private static void TryDeleteEmptyFolder(string folder)
    {
        try
        {
            if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any())
                Directory.Delete(folder);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
