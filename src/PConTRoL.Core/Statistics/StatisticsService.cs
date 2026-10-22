using System.Globalization;

namespace PConTRoL.Core.Statistics;

/// <summary>Kennzahlen eines Materials (oder aller Platten). Flächen in mm².</summary>
public sealed record MaterialStatistics(string Material, int Boards, double BoardArea, double UsedArea)
{
    /// <summary>Flächengewichtet: Summe genutzte Fläche / Summe Plattenfläche, in %.</summary>
    public double Utilization => BoardArea > 0 ? UsedArea / BoardArea * 100.0 : 0;

    public double Waste => BoardArea > 0 ? 100.0 - Utilization : 0;

    public double BoardAreaM2 => BoardArea / 1_000_000.0;
    public double UsedAreaM2 => UsedArea / 1_000_000.0;
    public double WasteAreaM2 => (BoardArea - UsedArea) / 1_000_000.0;
}

/// <summary>Auswertung der gespeicherten Platten – ohne Bezug zur Oberfläche.</summary>
public static class StatisticsService
{
    /// <summary>Platten im Zeitraum [fromUtc, toUtc). Null = offen.</summary>
    public static IEnumerable<BoardRecord> Filter(IEnumerable<BoardRecord> records, DateTime? fromUtc, DateTime? toUtc) =>
        records.Where(r => (fromUtc == null || r.TimeUtc >= fromUtc) && (toUtc == null || r.TimeUtc < toUtc));

    /// <summary>
    /// Je Material eine Zeile, alphabetisch. Materialien werden über <see cref="MaterialName.Key"/> zusammengefasst;
    /// angezeigt wird die Schreibweise der ältesten Platte. Neue Materialien erscheinen automatisch.
    /// </summary>
    public static IReadOnlyList<MaterialStatistics> ByMaterial(IEnumerable<BoardRecord> records) =>
        records
            .GroupBy(r => MaterialName.Key(r.Material))
            .Select(g =>
            {
                var name = g.OrderBy(r => r.TimeUtc).Select(r => MaterialName.Normalize(r.Material)).First();
                return new MaterialStatistics(name.Length > 0 ? name : BoardRecord.NoMaterial,
                    g.Count(), g.Sum(r => r.BoardArea), g.Sum(r => r.UsedArea));
            })
            .OrderBy(m => m.Material, StringComparer.Create(CultureInfo.GetCultureInfo("de-DE"), ignoreCase: true))
            .ToList();

    public static MaterialStatistics Total(IEnumerable<BoardRecord> records)
    {
        var list = records as IReadOnlyCollection<BoardRecord> ?? records.ToList();
        return new MaterialStatistics("Alle Materialien", list.Count, list.Sum(r => r.BoardArea), list.Sum(r => r.UsedArea));
    }
}
