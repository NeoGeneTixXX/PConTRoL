using System.Text.RegularExpressions;
using PConTRoL.Core.Parsing;

namespace PConTRoL.Core.Scanning;

public sealed class ScanResult
{
    public required IReadOnlyList<JobInfo> Stock { get; init; }

    /// <summary>In der Reihenfolge, in der die Maschine abarbeitet (Index 0 = läuft gerade).</summary>
    public required IReadOnlyList<JobInfo> Machine { get; init; }

    public string? RunningKey => MachineConfig.TopJobIsRunning && Machine.Count > 0 ? Machine[0].Key : null;
}

/// <summary>
/// Liest beide Ordner ein. Geparste Infos werden pro Datei (Pfad + Änderungsdatum + Größe) gecacht,
/// damit das Einlesen alle paar Sekunden auch über das Netzwerk billig bleibt.
/// </summary>
public sealed class FolderScanner
{
    private static readonly Regex BoardSuffix = new(@"_\d+$", RegexOptions.Compiled);

    private readonly AppPaths _paths;
    private readonly MachineSortOrder _sortOrder;
    private readonly Dictionary<string, (DateTime Hop, long HopLen, DateTime Lbl, NestFileInfo Info)> _cache =
        new(StringComparer.OrdinalIgnoreCase);

    public FolderScanner(AppPaths paths, MachineSortOrder sortOrder = MachineConfig.SortOrder)
    {
        _paths = paths;
        _sortOrder = sortOrder;
    }

    public ScanResult Scan()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var stock = new List<JobInfo>();
        foreach (var hop in Directory.EnumerateFiles(_paths.NestResult, "*.hop", SearchOption.AllDirectories))
        {
            var dir = Path.GetDirectoryName(hop)!;
            if (string.Equals(Path.GetFullPath(dir), Path.GetFullPath(_paths.NestResult), StringComparison.OrdinalIgnoreCase))
                dir = "";
            var job = Load(hop, Side.Stock, dir.Length == 0 ? null : Path.GetRelativePath(_paths.NestResult, dir), seen);
            if (job != null) stock.Add(job);
        }

        var machine = new List<JobInfo>();
        foreach (var hop in Directory.EnumerateFiles(_paths.Input, "*.hop", SearchOption.TopDirectoryOnly))
        {
            var job = Load(hop, Side.Machine, null, seen);
            if (job != null) machine.Add(job);
        }

        foreach (var stale in _cache.Keys.Where(k => !seen.Contains(k)).ToList())
            _cache.Remove(stale);

        return new ScanResult { Stock = stock, Machine = SortForMachine(machine, _sortOrder) };
    }

    /// <summary>
    /// Sortiert so, wie der JobManager abarbeitet: zuerst nach Production ID (aufsteigend, keine = 0),
    /// dann nach Änderungsdatum, bei gleicher Zeit nach Name.
    /// </summary>
    public static List<JobInfo> SortForMachine(IEnumerable<JobInfo> jobs, MachineSortOrder sortOrder)
    {
        var byId = jobs.OrderBy(j => j.ProductionId);
        var byDate = sortOrder == MachineSortOrder.OldestFirst
            ? byId.ThenBy(j => j.LastWriteUtc)
            : byId.ThenByDescending(j => j.LastWriteUtc);
        return byDate.ThenBy(j => j.Key, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private JobInfo? Load(string hop, Side side, string? folderNestJob, HashSet<string> seen)
    {
        try
        {
            var hopInfo = new FileInfo(hop);
            if (!hopInfo.Exists) return null;
            var key = Path.GetFileNameWithoutExtension(hop);
            string? lbl = Path.ChangeExtension(hop, ".lbl");
            var lblTime = DateTime.MinValue;
            if (File.Exists(lbl)) lblTime = File.GetLastWriteTimeUtc(lbl);
            else lbl = null;

            seen.Add(hop);
            NestFileInfo info;
            if (_cache.TryGetValue(hop, out var c) && c.Hop == hopInfo.LastWriteTimeUtc && c.HopLen == hopInfo.Length && c.Lbl == lblTime)
            {
                info = c.Info;
            }
            else
            {
                info = NestFileParser.Parse(hop, lbl);
                _cache[hop] = (hopInfo.LastWriteTimeUtc, hopInfo.Length, lblTime, info);
            }

            return new JobInfo
            {
                Key = key,
                Side = side,
                HopPath = hop,
                LblPath = lbl,
                NestJob = folderNestJob ?? DeriveNestJob(key, info),
                LastWriteUtc = hopInfo.LastWriteTimeUtc,
                BoardId = info.BoardId,
                BoardCount = info.BoardCount,
                Material = info.Material ?? "",
                DimX = info.DimX,
                DimY = info.DimY,
                DimZ = info.DimZ,
                PartsOnBoard = info.PartsOnBoard,
                Efficiency = info.Efficiency,
                Priority = info.Priority,
                ProductionNumber = info.ProductionId
                    ?? string.Join(", ", info.Parts.Select(p => p.Number).Where(n => n.Length > 0).Distinct()),
                ProductionId = HopProductionId.Parse(info.ProductionId) ?? 0,
                Parts = info.Parts,
                UsedArea = info.UsedArea,
                ContentHash = info.LblHash,
            };
        }
        catch (IOException)
        {
            return null; // Datei verschwindet gerade (Maschine fertig) – beim nächsten Durchlauf passt es wieder.
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Unterordner in _NestResult, aus dem der Auftrag kam. Quelle: HopFilename in der .lbl
    /// (…\_NestResult\&lt;Job&gt;\datei.hop), sonst Name der .nst, sonst Dateiname ohne "_01".
    /// </summary>
    public static string DeriveNestJob(string key, NestFileInfo info)
    {
        if (!string.IsNullOrWhiteSpace(info.OriginalHopPath))
        {
            var normalized = info.OriginalHopPath.Replace('/', '\\');
            var parts = normalized.Split('\\', StringSplitOptions.RemoveEmptyEntries);
            var idx = Array.FindLastIndex(parts, p => p.Equals(AppPaths.NestResultFolderName, StringComparison.OrdinalIgnoreCase));
            if (idx >= 0 && idx + 2 < parts.Length)
                return string.Join('\\', parts[(idx + 1)..^1]);
        }
        if (!string.IsNullOrWhiteSpace(info.NestFileName))
        {
            var nst = info.NestFileName.Replace('/', '\\').Split('\\', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
            if (!string.IsNullOrEmpty(nst)) return Path.GetFileNameWithoutExtension(nst);
        }
        return BoardSuffix.Replace(key, "");
    }
}
