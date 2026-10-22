using System.Text;
using System.Text.Json;

namespace PConTRoL.Core.Statistics;

/// <summary>
/// Dauerhafte Statistik im gemeinsamen Ordner (_PConTRoL\statistik.jsonl), eine JSON-Zeile pro Platte.
/// Nur anhängen, nie umschreiben: so können mehrere Rechner gleichzeitig schreiben, ohne sich zu stören.
/// Doppelte Zeilen (z. B. zwei Rechner erfassen dieselbe Platte) werden beim Lesen über die Id herausgefiltert.
/// </summary>
public sealed class StatisticsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };
    private readonly object _lock = new();
    private HashSet<string>? _ids;
    private long _idsLength = -1;

    public StatisticsStore(string path) => FilePath = path;

    public string FilePath { get; }

    /// <summary>Alle Platten, ohne Dubletten (erste Erfassung gilt). Kaputte Zeilen werden übersprungen.</summary>
    public IReadOnlyList<BoardRecord> Load()
    {
        lock (_lock)
        {
            var result = new List<BoardRecord>();
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!File.Exists(FilePath)) return result;

            foreach (var line in ReadLines())
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                BoardRecord? record;
                try
                {
                    record = JsonSerializer.Deserialize<BoardRecord>(line, JsonOptions);
                }
                catch (JsonException)
                {
                    continue; // z. B. halb geschriebene Zeile nach Stromausfall
                }
                if (record == null || record.Id.Length == 0 || !ids.Add(record.Id)) continue;
                result.Add(record);
            }
            return result;
        }
    }

    public bool Contains(string id)
    {
        lock (_lock) return KnownIds().Contains(id);
    }

    /// <summary>Speichert eine Platte. False, wenn sie schon erfasst war.</summary>
    public bool Add(BoardRecord record) => AddRange([record]) == 1;

    /// <summary>Speichert mehrere Platten in einem Rutsch und liefert die Anzahl der wirklich neuen.</summary>
    public int AddRange(IEnumerable<BoardRecord> records)
    {
        lock (_lock)
        {
            var ids = KnownIds();
            var fresh = records.Where(r => r.Id.Length > 0 && ids.Add(r.Id)).ToList();
            if (fresh.Count == 0) return 0;

            var sb = new StringBuilder();
            foreach (var r in fresh) sb.Append(JsonSerializer.Serialize(r, JsonOptions)).Append('\n');
            Append(sb.ToString());
            _idsLength = new FileInfo(FilePath).Length;
            return fresh.Count;
        }
    }

    private HashSet<string> KnownIds()
    {
        var length = File.Exists(FilePath) ? new FileInfo(FilePath).Length : 0;
        if (_ids == null || length != _idsLength)
        {
            _ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (length > 0)
            {
                foreach (var line in ReadLines())
                {
                    try
                    {
                        if (JsonSerializer.Deserialize<BoardRecord>(line, JsonOptions) is { Id.Length: > 0 } r) _ids.Add(r.Id);
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
