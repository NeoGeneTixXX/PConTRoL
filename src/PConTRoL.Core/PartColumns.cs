namespace PConTRoL.Core;

/// <summary>Eine Spalte der Teileliste, wie der Benutzer sie eingestellt hat (Reihenfolge = Reihenfolge der Liste).</summary>
public sealed class PartColumnSetting
{
    public string Key { get; set; } = "";
    public string Header { get; set; } = "";
    public bool Visible { get; set; } = true;
}

/// <summary>Die möglichen Spalten der Teileliste unten im Hauptfenster (Daten aus der .lbl).</summary>
public static class PartColumnCatalog
{
    /// <param name="Width">Breite in Pixeln; null = füllt den restlichen Platz.</param>
    public sealed record Def(string Key, string DefaultHeader, string Path, double? Width);

    public static readonly IReadOnlyList<Def> All =
    [
        new("rd", "RD-Nr.", nameof(PartInfo.RdNumber), 120),
        new("description", "Bezeichnung", nameof(PartInfo.Description), null),
        new("article", "Artikel", nameof(PartInfo.Article), 200),
        new("size", "Maß", nameof(PartInfo.Size), 120),
        new("number", "Nr.", nameof(PartInfo.Number), 90),
    ];

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
