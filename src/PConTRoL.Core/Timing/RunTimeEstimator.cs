using System.Globalization;
using PConTRoL.Core.Statistics;

namespace PConTRoL.Core.Timing;

/// <summary>Worauf eine Schätzung beruht, vom genauesten zum gröbsten.</summary>
public enum EstimateBasis
{
    /// <summary>Gleiches Material, gleiches Plattenmaß, gleich viele Teile.</summary>
    Exact,

    /// <summary>Gleiches Material und Plattenmaß, Teilezahl verrechnet.</summary>
    SameBoard,

    /// <summary>Gleiches Material, beliebiges Maß.</summary>
    SameMaterial,

    /// <summary>Alle gemessenen Platten.</summary>
    All,
}

public sealed record RunTimeEstimate(TimeSpan Duration, int Samples, EstimateBasis Basis);

/// <summary>
/// Schätzt die Laufzeit einer Platte aus den bisher gemessenen. Je mehr Messungen, desto genauer:
/// Es wird immer die genaueste Gruppe genommen, die genug Messungen hat. Gezählt werden nur die jüngsten
/// <see cref="RecentCount"/> einer Gruppe, damit sich die Schätzung an Änderungen (Werkzeug, Bediener) anpasst.
/// Der Median macht Ausreißer (kurze Pause, Störung) unschädlich.
/// </summary>
public sealed class RunTimeEstimator
{
    public const int RecentCount = 30;

    /// <summary>Ab so vielen Messungen gilt eine Gruppe als belastbar.</summary>
    public const int SolidSamples = 3;

    private readonly Dictionary<string, List<RunTimeRecord>> _exact = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<RunTimeRecord>> _board = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<RunTimeRecord>> _material = new(StringComparer.Ordinal);
    private readonly List<RunTimeRecord> _all;

    public RunTimeEstimator(IEnumerable<RunTimeRecord> records)
    {
        _all = records.Where(r => r.Seconds > 0).OrderByDescending(r => r.EndUtc).ToList();
        foreach (var r in _all)
        {
            Add(_exact, ExactKey(r.Material, r.DimX, r.DimY, r.DimZ, r.Parts), r);
            Add(_board, BoardKey(r.Material, r.DimX, r.DimY, r.DimZ), r);
            Add(_material, MaterialName.Key(r.Material), r);
        }
    }

    public int Count => _all.Count;

    public RunTimeEstimate? Estimate(JobInfo job)
    {
        var parts = RunTimeRecord.PartsOf(job);
        var dx = job.DimX ?? 0;
        var dy = job.DimY ?? 0;
        var dz = job.DimZ ?? 0;

        var candidates = new (EstimateBasis Basis, List<RunTimeRecord>? Group)[]
        {
            (EstimateBasis.Exact, _exact.GetValueOrDefault(ExactKey(job.Material, dx, dy, dz, parts))),
            (EstimateBasis.SameBoard, _board.GetValueOrDefault(BoardKey(job.Material, dx, dy, dz))),
            (EstimateBasis.SameMaterial, _material.GetValueOrDefault(MaterialName.Key(job.Material))),
            (EstimateBasis.All, _all),
        };

        // Die genaueste belastbare Gruppe, sonst die genaueste mit überhaupt einer Messung.
        var pick = candidates.FirstOrDefault(c => c.Group is { Count: >= SolidSamples });
        if (pick.Group == null) pick = candidates.FirstOrDefault(c => c.Group is { Count: > 0 });
        if (pick.Group == null) return null;

        var recent = pick.Group.Take(RecentCount).ToList();
        var seconds = pick.Basis == EstimateBasis.Exact ? Median(recent) : ByParts(recent, parts);
        seconds = Math.Max(seconds, RunTimeRecord.MinPlausible.TotalSeconds);
        return new RunTimeEstimate(TimeSpan.FromSeconds(Math.Round(seconds)), recent.Count, pick.Basis);
    }

    /// <summary>
    /// Zeit = Grundzeit + Zeit je Teil (Ausgleichsgerade), wenn die Messungen verschiedene Teilezahlen haben
    /// und daraus etwas Sinnvolles herauskommt. Sonst der Median.
    /// </summary>
    internal static double ByParts(IReadOnlyList<RunTimeRecord> records, int parts)
    {
        var median = Median(records);
        if (records.Count < SolidSamples || parts <= 0 || records.Select(r => r.Parts).Distinct().Count() < 2) return median;

        var meanX = records.Average(r => (double)r.Parts);
        var meanY = records.Average(r => r.Seconds);
        var sxx = records.Sum(r => (r.Parts - meanX) * (r.Parts - meanX));
        if (sxx <= 0) return median;
        var slope = records.Sum(r => (r.Parts - meanX) * (r.Seconds - meanY)) / sxx;
        var intercept = meanY - slope * meanX;
        if (slope < 0 || intercept < 0) return median;

        // Nicht weit über die gemessenen Teilezahlen hinaus hochrechnen.
        var min = records.Min(r => r.Parts);
        var max = records.Max(r => r.Parts);
        var x = Math.Clamp(parts, min / 2.0, max * 2.0);
        return intercept + slope * x;
    }

    internal static double Median(IReadOnlyCollection<RunTimeRecord> records)
    {
        var sorted = records.Select(r => r.Seconds).Order().ToList();
        var mid = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2.0;
    }

    private static void Add(Dictionary<string, List<RunTimeRecord>> map, string key, RunTimeRecord r)
    {
        if (!map.TryGetValue(key, out var list)) map[key] = list = [];
        list.Add(r);
    }

    private static string BoardKey(string? material, double dx, double dy, double dz) =>
        string.Create(CultureInfo.InvariantCulture, $"{MaterialName.Key(material)}|{Math.Round(dx)}x{Math.Round(dy)}x{Math.Round(dz, 1)}");

    private static string ExactKey(string? material, double dx, double dy, double dz, int parts) =>
        BoardKey(material, dx, dy, dz) + "|" + parts.ToString(CultureInfo.InvariantCulture);
}
