namespace PConTRoL.Core;

/// <summary>Eine Spalte der Teileliste, wie der Benutzer sie eingestellt hat (Reihenfolge = Reihenfolge der Liste).</summary>
public sealed class PartColumnSetting
{
    public string Key { get; set; } = "";
    public string Header { get; set; } = "";
    public bool Visible { get; set; } = true;
}

/// <summary>Eine Zeile der Teileliste: gleiche Teile zusammengefasst, Count = Stückzahl (Spalte „ST“).</summary>
public sealed record PartRow(int Count, string RdNumber, string Description, string Article, string Size, string Number);

/// <summary>Die möglichen Spalten der Teileliste unten im Hauptfenster (Daten aus der .lbl).</summary>
public static class PartColumnCatalog
{
    /// <summary>Feste erste Spalte mit der Stückzahl; nicht einstellbar.</summary>
    public const string CountHeader = "ST";

    /// <param name="Path">Eigenschaft in <see cref="PartRow"/> (für die Bindung).</param>
    /// <param name="Width">Breite in Pixeln; null = füllt den restlichen Platz.</param>
    public sealed record Def(string Key, string DefaultHeader, string Path, double? Width, Func<PartInfo, string> Value);

    public static readonly IReadOnlyList<Def> All =
    [
        new("rd", "RD-Nr.", nameof(PartRow.RdNumber), 120, p => p.RdNumber),
        new("description", "Bezeichnung", nameof(PartRow.Description), null, p => p.Description),
        new("article", "Artikel", nameof(PartRow.Article), 200, p => p.Article),
        new("size", "Maß", nameof(PartRow.Size), 120, p => p.Size),
        new("number", "Nr.", nameof(PartRow.Number), 90, p => p.Number),
    ];

    /// <summary>
    /// Fasst Teile zusammen, die in allen angezeigten Spalten gleich sind (Reihenfolge: erstes Vorkommen).
    /// Ausgeblendete Spalten zählen nicht mit: Was man nicht sieht, unterscheidet die Zeilen auch nicht.
    /// </summary>
    public static List<PartRow> Group(IEnumerable<PartInfo> parts, IEnumerable<string> visibleKeys)
    {
        var defs = visibleKeys.Select(Find).OfType<Def>().ToList();
        var rows = new List<(string Signature, PartInfo First, int Count)>();
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var part in parts)
        {
            var signature = string.Join('\u001f', defs.Select(d => (d.Value(part) ?? "").Trim()));
            if (index.TryGetValue(signature, out var i))
            {
                rows[i] = (signature, rows[i].First, rows[i].Count + 1);
                continue;
            }
            index[signature] = rows.Count;
            rows.Add((signature, part, 1));
        }
        return rows.Select(r => new PartRow(r.Count, r.First.RdNumber, r.First.Description, r.First.Article, r.First.Size, r.First.Number)).ToList();
    }

    public static Def? Find(string key) => All.FirstOrDefault(d => d.Key == key);

    public static List<PartColumnSetting> Defaults() =>
        All.Select(d => new PartColumnSetting { Key = d.Key, Header = d.DefaultHeader }).ToList();

    /// <summary>
    /// Bereinigt gespeicherte Einstellungen: unbekannte und doppelte Spalten fallen weg, neue Spalten kommen ans Ende,
    /// leere Namen bekommen den Standardnamen.
    /// </summary>
    public static List<PartColumnSetting> Normalize(IEnumerable<PartColumnSetting?>? saved)
    {
        var result = new List<PartColumnSetting>();
        foreach (var s in saved ?? [])
        {
            var def = s == null ? null : Find(s.Key);
            if (def == null || result.Any(r => r.Key == def.Key)) continue;
            var header = s!.Header?.Trim() ?? "";
            result.Add(new PartColumnSetting { Key = def.Key, Header = header.Length > 0 ? header : def.DefaultHeader, Visible = s.Visible });
        }
        foreach (var def in All.Where(d => result.All(r => r.Key != d.Key)))
            result.Add(new PartColumnSetting { Key = def.Key, Header = def.DefaultHeader });
        return result;
    }

    /// <summary>
    /// Neue Anzeige-Reihenfolge der sichtbaren Spalten übernehmen (z. B. nach Ziehen am Spaltenkopf).
    /// Ausgeblendete Spalten bleiben an ihrem Platz.
    /// </summary>
    public static List<PartColumnSetting> Reorder(IReadOnlyList<PartColumnSetting> columns, IEnumerable<string> visibleKeysInOrder)
    {
        var byKey = columns.ToDictionary(c => c.Key);
        var shown = new Queue<string>(visibleKeysInOrder.Where(byKey.ContainsKey));
        return columns.Select(c => c.Visible && shown.Count > 0 ? byKey[shown.Dequeue()] : c).ToList();
    }
}
