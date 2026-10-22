using System.Globalization;

namespace PConTRoL.Core.Statistics;

/// <summary>CSV-Export für Excel (deutsches Format: Semikolon, Komma als Dezimaltrennzeichen).</summary>
public static class StatisticsCsv
{
    private static readonly CultureInfo De = CultureInfo.GetCultureInfo("de-DE");

    public static void WriteSummary(TextWriter w, IEnumerable<MaterialStatistics> materials, MaterialStatistics total)
    {
        w.WriteLine("Material;Platten;Ausnutzung %;Verschnitt %;Plattenfläche m²;Genutzte Fläche m²;Verschnitt m²");
        foreach (var m in materials.Append(total))
            w.WriteLine(string.Join(';', Q(m.Material), m.Boards.ToString(De), N(m.Utilization, 2), N(m.Waste, 2),
                N(m.BoardAreaM2, 3), N(m.UsedAreaM2, 3), N(m.WasteAreaM2, 3)));
    }

    public static void WriteBoards(TextWriter w, IEnumerable<BoardRecord> records)
    {
        w.WriteLine("Zeitpunkt;Material;Plattenfläche m²;Genutzte Fläche m²;Ausnutzung %;Verschnitt %;Nest-Job;Datei;Quelle;Rechner");
        foreach (var r in records.OrderBy(r => r.TimeUtc))
            w.WriteLine(string.Join(';', r.TimeUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", De), Q(r.Material),
                N(r.BoardArea / 1_000_000.0, 3), N(r.UsedArea / 1_000_000.0, 3), N(r.Utilization, 2), N(r.Waste, 2),
                Q(r.NestJob), Q(r.File), Q(r.Source), Q(r.Computer)));
    }

    private static string N(double value, int decimals) => Math.Round(value, decimals).ToString("0." + new string('0', decimals), De);

    private static string Q(string text) =>
        text.IndexOfAny([';', '"', '\n', '\r']) >= 0 ? "\"" + text.Replace("\"", "\"\"") + "\"" : text;
}
