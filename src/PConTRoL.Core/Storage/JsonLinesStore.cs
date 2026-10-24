using System.Text;
using System.Text.Json;

namespace PConTRoL.Core.Storage;

/// <summary>
/// Dauerhafte Liste im gemeinsamen Ordner, eine JSON-Zeile pro Eintrag.
/// Nur anhängen, nie umschreiben: so können mehrere Rechner gleichzeitig schreiben, ohne sich zu stören.
/// Doppelte Zeilen (z. B. zwei Rechner erfassen dasselbe) werden beim Lesen über die Id herausgefiltert.
/// </summary>
public class JsonLinesStore<T> where T : class
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };
    private readonly object _lock = new();
    private readonly Func<T, string> _id;
    private HashSet<string>? _ids;
    private long _idsLength = -1;
    private IReadOnlyList<T>? _loaded;
    private long _loadedLength = -1;

    public JsonLinesStore(string path, Func<T, string> id)
    {
        FilePath = path;
        _id = id;
    }

    public string FilePath { get; }

    /// <summary>
    /// Alle Einträge, ohne Dubletten (erste Erfassung gilt). Kaputte Zeilen werden übersprungen.
    /// Solange die Datei nicht wächst, kommt die zuletzt gelesene Liste zurück (billig für häufige Aufrufe).
    /// </summary>
    public IReadOnlyList<T> Load()
    {
        lock (_lock)
        {
            var length = CurrentLength();
            if (_loaded != null && length == _loadedLength) return _loaded;

            var result = new List<T>();
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (length > 0)
            {
                foreach (var line in ReadLines())
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    T? record;
                    try
                    {
                        record = JsonSerializer.Deserialize<T>(line, JsonOptions);
                    }
                    catch (JsonException)
                    {
                        continue; // z. B. halb geschriebene Zeile nach Stromausfall
                    }
                    if (record == null || _id(record).Length == 0 || !ids.Add(_id(record))) continue;
                    result.Add(record);
                }
            }
            _loaded = result;
            _loadedLength = length;
            return result;
        }
    }

    public bool Contains(string id)
    {
        lock (_lock) return KnownIds().Contains(id);
    }

    /// <summary>Speichert einen Eintrag. False, wenn er schon erfasst war.</summary>
    public bool Add(T record) => AddRange([record]) == 1;

    /// <summary>Speichert mehrere Einträge in einem Rutsch und liefert die Anzahl der wirklich neuen.</summary>
    public int AddRange(IEnumerable<T> records)
    {
        lock (_lock)
        {
            var ids = KnownIds();
            var fresh = records.Where(r => _id(r).Length > 0 && ids.Add(_id(r))).ToList();
            if (fresh.Count == 0) return 0;

            var sb = new StringBuilder();
            foreach (var r in fresh) sb.Append(JsonSerializer.Serialize(r, JsonOptions)).Append('\n');
            Append(sb.ToString());
            _idsLength = new FileInfo(FilePath).Length;
            return fresh.Count;
        }
    }

    private long CurrentLength() => File.Exists(FilePath) ? new FileInfo(FilePath).Length : 0;

    private HashSet<string> KnownIds()
    {
        var length = CurrentLength();
        if (_ids == null || length != _idsLength)
        {
            _ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (length > 0)
            {
                foreach (var line in ReadLines())
                {
                    try
                    {
                        if (JsonSerializer.Deserialize<T>(line, JsonOptions) is { } r && _id(r).Length > 0) _ids.Add(_id(r));
                    }
                    catch (JsonException)
                    {
                    }
                }
            }
            _idsLength = length;
        }
        return _ids;
    }

    private List<string> ReadLines()
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var stream = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                var lines = new List<string>();
                while (reader.ReadLine() is { } line) lines.Add(line);
                return lines;
            }
            catch (IOException) when (attempt < 10)
            {
                Thread.Sleep(50);
            }
        }
    }

    private void Append(string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var stream = new FileStream(FilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                using var writer = new StreamWriter(stream, new UTF8Encoding(false));
                writer.Write(text);
                return;
            }
            catch (IOException) when (attempt < 10)
            {
                Thread.Sleep(50);
            }
        }
    }
}
