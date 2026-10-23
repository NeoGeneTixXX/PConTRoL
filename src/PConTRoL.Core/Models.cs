namespace PConTRoL.Core;

/// <summary>Linke Seite = Auftragsvorrat (_NestResult), rechte Seite = Maschine (_Input).</summary>
public enum Side
{
    Stock,
    Machine,
}

/// <summary>Ein Teil auf einer Platte (aus der .lbl). DimX/DimY = Maße des Teils in mm (für die Fläche).</summary>
public sealed record PartInfo(
    string RdNumber,
    string Description,
    string Article,
    string Size,
    string Number,
    double? DimX = null,
    double? DimY = null);

/// <summary>Ein Auftrag = eine Platte = eine .hop-Datei (mit .lbl).</summary>
public sealed class JobInfo
{
    /// <summary>Dateiname der .hop ohne Endung. Eindeutig je Seite.</summary>
    public required string Key { get; init; }
    public required Side Side { get; init; }
    public required string HopPath { get; init; }
    public string? LblPath { get; init; }

    /// <summary>Name des Unterordners in _NestResult (= Nest-Job).</summary>
    public required string NestJob { get; init; }

    public DateTime LastWriteUtc { get; init; }

    public int? BoardId { get; init; }
    public int? BoardCount { get; init; }
    public string Material { get; init; } = "";
    public double? DimX { get; init; }
    public double? DimY { get; init; }
    public double? DimZ { get; init; }
    public int? PartsOnBoard { get; init; }
    public double? Efficiency { get; init; }
    public int? Priority { get; init; }
    public string ProductionNumber { get; init; } = "";

    /// <summary>_hhdata_ProductionID aus der .hop (0 = keine). Der JobManager sortiert zuerst danach.</summary>
    public long ProductionId { get; init; }

    /// <summary>Summe der Teileflächen in mm² (null, wenn nicht aus der .lbl ableitbar).</summary>
    public double? UsedArea { get; init; }

    /// <summary>SHA-256 der .lbl – erkennt dieselbe Platte wieder (z. B. im Archiv).</summary>
    public string? ContentHash { get; init; }
    public IReadOnlyList<PartInfo> Parts { get; init; } = [];

    public string BoardText => BoardId is { } id
        ? (BoardCount is { } c ? $"{id} / {c}" : id.ToString())
        : "";

    public string DimsText => DimX is null && DimY is null && DimZ is null
        ? ""
        : $"{Fmt(DimX)} × {Fmt(DimY)} × {Fmt(DimZ)}";

    private static string Fmt(double? v) =>
        v?.ToString("0.#", System.Globalization.CultureInfo.GetCultureInfo("de-DE")) ?? "?";
}
