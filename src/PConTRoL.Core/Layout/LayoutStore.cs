using System.Text.Json;

namespace PConTRoL.Core.Layout;

/// <summary>
/// Speichert Reihenfolge und Gruppen gemeinsam für alle Rechner im Hauptordner (_PConTRoL\reihenfolge.json).
/// Schreiben erfolgt atomar (temporäre Datei + Umbenennen).
/// </summary>
public sealed class LayoutStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _path;

    public LayoutStore(string path) => _path = path;

    public DateTime LastLoadedWriteUtc { get; private set; } = DateTime.MinValue;

    public bool HasChangedOnDisk() =>
        File.Exists(_path) && File.GetLastWriteTimeUtc(_path) != LastLoadedWriteUtc;

    public LayoutDocument Load()
    {
        if (!File.Exists(_path))
        {
            LastLoadedWriteUtc = DateTime.MinValue;
            return new LayoutDocument();
        }

        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var writeTime = File.GetLastWriteTimeUtc(_path);
                using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var doc = JsonSerializer.Deserialize<LayoutDocument>(stream, JsonOptions) ?? new LayoutDocument();
                Normalize(doc.Stock);
                Normalize(doc.Machine);
                LastLoadedWriteUtc = writeTime;
                return doc;
            }
            catch (IOException) when (attempt < 5)
            {
                Thread.Sleep(100);
            }
            catch (JsonException)
            {
                // Kaputte Datei nicht überschreiben, sondern beiseitelegen.
                var backup = _path + ".defekt-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
                try { File.Copy(_path, backup, overwrite: false); } catch (IOException) { }
                LastLoadedWriteUtc = File.GetLastWriteTimeUtc(_path);
                return new LayoutDocument();
            }
        }
    }

    public void Save(LayoutDocument doc)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var tmp = _path + ".tmp-" + Environment.MachineName;
        File.WriteAllText(tmp, JsonSerializer.Serialize(doc, JsonOptions));
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                File.Move(tmp, _path, overwrite: true);
                LastLoadedWriteUtc = File.GetLastWriteTimeUtc(_path);
                return;
            }
            catch (IOException) when (attempt < 10)
            {
                Thread.Sleep(100);
            }
        }
    }

    private static void Normalize(SideLayout side)
    {
        side.Order ??= [];
        side.Groups ??= [];
        side.Membership = new Dictionary<string, string>(side.Membership ?? [], StringComparer.OrdinalIgnoreCase);
    }
}
