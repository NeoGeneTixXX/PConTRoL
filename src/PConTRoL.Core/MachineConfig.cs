namespace PConTRoL.Core;

/// <summary>In welcher Reihenfolge die Maschine die Dateien in <c>_Input</c> abarbeitet.</summary>
public enum MachineSortOrder
{
    /// <summary>Älteste Datei (kleinstes Änderungsdatum) steht oben und wird zuerst bearbeitet.</summary>
    OldestFirst,

    /// <summary>Neueste Datei (größtes Änderungsdatum) steht oben und wird zuerst bearbeitet.</summary>
    NewestFirst,
}

/// <summary>
/// Feste Eigenschaften der Maschine. Bewusst KEINE Einstellung, sondern Konstanten im Code.
/// </summary>
public static class MachineConfig
{
    /// <summary>
    /// Sortierrichtung der Maschine (HOLZ-HER NEXTEC, Produktionsliste).
    ///
    /// Ermittelt am 22.10.2026 per Test an der Maschine: Dateien mit dem neuesten
    /// Änderungsdatum stehen in der Produktionsliste ganz unten ("zuletzt hinzugefügt").
    /// Die Maschine arbeitet also die ÄLTESTE Datei zuerst ab.
    ///
    /// Sollte sich das je ändern (Software-Update der Maschine), nur hier umstellen.
    /// Die gesamte Datumslogik (TimestampPlanner) richtet sich nach diesem Wert.
    /// </summary>
    public const MachineSortOrder SortOrder = MachineSortOrder.OldestFirst;

    /// <summary>
    /// Der oberste Auftrag in <c>_Input</c> ist IMMER der, den die Maschine gerade bearbeitet
    /// (Auskunft Maschinenbediener). Er wird nie verschoben und sein Datum nie geändert.
    /// </summary>
    public const bool TopJobIsRunning = true;
}
