using System.Text.Json;

namespace PConTRoL.Core.Kanban;

/// <summary>
/// Eine Kanban-Kachel: feste Zusammenstellung von Platten, die per Klick als Gruppe (Name = Kachelname)
/// an die Maschine geht. Die Originale liegen in <c>&lt;Hauptordner&gt;\Kanban\&lt;Folder&gt;\</c> und bleiben dort;
/// zur Maschine gehen immer Kopien, die Kachel ist also beliebig oft nutzbar.
/// </summary>
public sealed class KanbanTile
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";

    /// <summary>Unterordner in Kanban\ (beim Anlegen aus dem Namen gebildet, bleibt beim Umbenennen gleich).</summary>
    public string Folder { get; set; } = "";

    /// <summary>Dateinamen der .hop in Kanban\Folder\, in der Reihenfolge, in der sie an die Maschine gehen.</summary>
    public List<string> Files { get; set; } = [];
}

public sealed class KanbanDocument
{
    public List<KanbanTile> Tiles { get; set; } = [];
}

/// <summary>Kacheln gemeinsam für alle Rechner in <c>_PConTRoL\kanban.json</c> (atomar geschrieben).</summary>
public sealed class KanbanStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _path;
    private DateTime _loadedWriteUtc = DateTime.MinValue;
    private KanbanDocument? _cached;

    public KanbanStore(string path) => _path = path;

    /// <summary>Liest neu, wenn sich die Datei geändert hat (sonst aus dem Speicher).</summary>
    public KanbanDocument Load()
    {
        var writeTime = File.Exists(_path) ? File.GetLastWriteTimeUtc(_path) : DateTime.MinValue;
        if (_cached != null && writeTime == _loadedWriteUtc) return Clone(_cached);

        KanbanDocument doc;
        try
        {
            doc = writeTime == DateTime.MinValue
                ? new KanbanDocument()
                : JsonSerializer.Deserialize<KanbanDocument>(File.ReadAllText(_path), JsonOptions) ?? new KanbanDocument();
        }
        catch (JsonException)
        {
            // Kaputte Datei nicht überschreiben, sondern beiseitelegen.
            try { File.Copy(_path, _path + ".defekt-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"), overwrite: false); } catch (IOException) { }
            doc = new KanbanDocument();
        }
        doc.Tiles ??= [];
        foreach (var t in doc.Tiles) t.Files ??= [];
        _cached = doc;
        _loadedWriteUtc = writeTime;
        return Clone(doc);
    }

    public void Save(KanbanDocument doc)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var tmp = _path + ".tmp-" + Environment.MachineName;
        File.WriteAllText(tmp, JsonSerializer.Serialize(doc, JsonOptions));
        File.Move(tmp, _path, overwrite: true);
        _cached = Clone(doc);
        _loadedWriteUtc = File.GetLastWriteTimeUtc(_path);
    }

    private static KanbanDocument Clone(KanbanDocument d) =>
        JsonSerializer.Deserialize<KanbanDocument>(JsonSerializer.Serialize(d, JsonOptions), JsonOptions)!;
}
