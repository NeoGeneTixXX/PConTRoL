namespace PConTRoL.Core.Statistics;

/// <summary>Eine fertig bearbeitete Platte, wie sie dauerhaft in der Statistik steht. Flächen in mm².</summary>
public sealed class BoardRecord
{
    public const string SourceMachine = "Maschine";
    public const string SourceArchive = "Archiv";
    public const string NoMaterial = "(ohne Material)";

    /// <summary>SHA-256 der .lbl. Gleiche Platte = gleiche Id, egal ob live erfasst oder aus dem Archiv.</summary>
    public string Id { get; set; } = "";

    /// <summary>Zeitpunkt der Bearbeitung (live: als die Maschine fertig war; Archiv: Dateidatum).</summary>
    public DateTime TimeUtc { get; set; }

    public string Material { get; set; } = "";
    public double BoardArea { get; set; }
    public double UsedArea { get; set; }
    public double Utilization { get; set; }
    public double Waste { get; set; }
    public string NestJob { get; set; } = "";
    public string File { get; set; } = "";
    public string Source { get; set; } = "";
    public string Computer { get; set; } = "";

    /// <summary>Baut einen Datensatz. Null, wenn die Platte nicht auswertbar ist (keine .lbl, Maße fehlen, Unsinn).</summary>
    public static BoardRecord? Create(string? id, DateTime timeUtc, string? material, double? dimX, double? dimY,
        double? usedArea, string nestJob, string file, string source)
    {
        if (string.IsNullOrEmpty(id) || dimX is not > 0 || dimY is not > 0 || usedArea is not > 0) return null;
        var board = dimX.Value * dimY.Value;
        // Etwas Toleranz für Rundung; deutlich mehr Teile- als Plattenfläche ist ein Datenfehler.
        if (usedArea.Value > board * 1.01) return null;

        var used = Math.Min(usedArea.Value, board);
        var utilization = used / board * 100.0;
        var name = MaterialName.Normalize(material);
        return new BoardRecord
        {
            Id = id,
            TimeUtc = timeUtc,
            Material = name.Length > 0 ? name : NoMaterial,
            BoardArea = board,
            UsedArea = used,
            Utilization = Math.Round(utilization, 3),
            Waste = Math.Round(100.0 - utilization, 3),
            NestJob = nestJob,
            File = file,
            Source = source,
            Computer = Environment.MachineName,
        };
    }

    public static BoardRecord? FromJob(JobInfo job, string source, DateTime timeUtc) =>
        Create(job.ContentHash, timeUtc, job.Material, job.DimX, job.DimY, job.UsedArea, job.NestJob, job.Key, source);
}
