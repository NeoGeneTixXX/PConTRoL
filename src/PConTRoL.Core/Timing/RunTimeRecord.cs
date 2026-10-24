using PConTRoL.Core.Statistics;
using PConTRoL.Core.Storage;

namespace PConTRoL.Core.Timing;

/// <summary>
/// Wie lange die Maschine für eine Platte gebraucht hat: von „ist oben in _Input, läuft“ bis „fertig, aus _Input weg“.
/// Darin steckt auch das Auflegen und Abräumen – genau die Zeit, die für die Planung zählt.
/// </summary>
public sealed class RunTimeRecord
{
    /// <summary>Kürzer ist kein echter Lauf (z. B. sofort wieder entfernt).</summary>
    public static readonly TimeSpan MinPlausible = TimeSpan.FromSeconds(20);

    /// <summary>Länger heißt fast immer Pause, Feierabend oder Störung – das würde jede Schätzung verfälschen.</summary>
    public static readonly TimeSpan MaxPlausible = TimeSpan.FromHours(4);

    /// <summary>Wie bei der Statistik: SHA-256 der .lbl mit Production ID. Mehrere Rechner erfassen dieselbe Platte nur einmal.</summary>
    public string Id { get; set; } = "";

    public DateTime StartUtc { get; set; }
    public DateTime EndUtc { get; set; }
    public double Seconds { get; set; }

    public string Material { get; set; } = "";
    public double DimX { get; set; }
    public double DimY { get; set; }
    public double DimZ { get; set; }

    /// <summary>Teile auf der Platte (PartsOnBoard aus der .lbl, sonst die Anzahl der Teile-Abschnitte).</summary>
    public int Parts { get; set; }

    public string NestJob { get; set; } = "";
    public string File { get; set; } = "";
    public string Computer { get; set; } = "";

    public TimeSpan Duration => TimeSpan.FromSeconds(Seconds);

    public static int PartsOf(JobInfo job) => job.PartsOnBoard is > 0 ? job.PartsOnBoard.Value : job.Parts.Count;

    /// <summary>Null, wenn die Platte nicht erkennbar ist oder die Dauer nicht plausibel.</summary>
    public static RunTimeRecord? FromJob(JobInfo job, DateTime startUtc, DateTime endUtc)
    {
        var duration = endUtc - startUtc;
        if (duration < MinPlausible || duration > MaxPlausible) return null;

        var id = job.ContentHash is { } hash
            ? (job.ProductionId > 0 ? $"{hash}-{job.ProductionId}" : hash)
            : (job.ProductionId > 0 ? $"{job.Key}-{job.ProductionId}" : null);
        if (id == null) return null;

        return new RunTimeRecord
        {
            Id = id,
            StartUtc = startUtc,
            EndUtc = endUtc,
            Seconds = Math.Round(duration.TotalSeconds, 1),
            Material = MaterialName.Normalize(job.Material),
            DimX = job.DimX ?? 0,
            DimY = job.DimY ?? 0,
            DimZ = job.DimZ ?? 0,
            Parts = PartsOf(job),
            NestJob = job.NestJob,
            File = job.Key,
            Computer = Environment.MachineName,
        };
    }
}

/// <summary>Gemessene Laufzeiten im gemeinsamen Ordner (_PConTRoL\laufzeiten.jsonl), für alle Rechner gleich.</summary>
public sealed class RunTimeStore(string path) : JsonLinesStore<RunTimeRecord>(path, r => r.Id);
