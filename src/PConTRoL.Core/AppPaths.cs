namespace PConTRoL.Core;

/// <summary>Alle Pfade, die aus dem Hauptordner abgeleitet werden.</summary>
public sealed class AppPaths
{
    public const string InputFolderName = "_Input";
    public const string NestResultFolderName = "_NestResult";

    /// <summary>Gemeinsamer Ordner für Reihenfolge, Gruppen und Log (für alle Rechner gleich).</summary>
    public const string SharedFolderName = "_PConTRoL";

    public AppPaths(string root)
    {
        Root = Path.GetFullPath(root);
        Input = Path.Combine(Root, InputFolderName);
        NestResult = Path.Combine(Root, NestResultFolderName);
        Shared = Path.Combine(Root, SharedFolderName);
    }

    public string Root { get; }
    public string Input { get; }
    public string NestResult { get; }
    public string Shared { get; }
    public string LayoutFile => Path.Combine(Shared, "reihenfolge.json");

    /// <summary>Originale der Kanban-Kacheln (je Kachel ein Unterordner). Von hier gehen nur Kopien an die Maschine.</summary>
    public string Kanban => Path.Combine(Root, KanbanFolderName);

    public const string KanbanFolderName = "Kanban";

    /// <summary>Kacheldefinitionen (für alle Rechner gemeinsam).</summary>
    public string KanbanFile => Path.Combine(Shared, "kanban.json");

    /// <summary>Zwischenablage für Kanban-Kopien, bevor sie nach _Input wandern (gleiches Laufwerk = Verschieben ist atomar).</summary>
    public string KanbanStaging => Path.Combine(Shared, "kanban-tmp");

    /// <summary>Gelöschte Aufträge landen hier (je Löschvorgang ein Unterordner mit Datum/Uhrzeit), nicht endgültig weg.</summary>
    public string RecycleBin => Path.Combine(Shared, "Papierkorb");

    /// <summary>Sperrdatei, damit die Automatik nicht auf zwei Rechnern gleichzeitig nachfüllt.</summary>
    public string AutoLockFile => Path.Combine(Shared, "automatik.lock");
    public string LogFolder => Shared;

    /// <summary>Statistik aller fertigen Platten (eine JSON-Zeile pro Platte, für alle Rechner gemeinsam).</summary>
    public string StatisticsFile => Path.Combine(Shared, "statistik.jsonl");

    public bool IsValid => Directory.Exists(Input) && Directory.Exists(NestResult);

    public string NestJobFolder(string nestJob) => Path.Combine(NestResult, nestJob);
}
