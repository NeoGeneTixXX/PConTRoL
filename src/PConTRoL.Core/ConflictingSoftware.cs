using System.Diagnostics;

namespace PConTRoL.Core;

/// <summary>
/// HOLZ-HER Production Control darf nicht neben PConTRoL laufen: Es verschiebt selbst Aufträge nach _Input
/// und schreibt Production IDs und Prioritäten in die .hop. Beides zusammen bringt die Reihenfolge an der Maschine durcheinander.
/// Der JobManager an der Maschine ist KEIN Konflikt und wird nie angefasst.
/// </summary>
public static class ConflictingSoftware
{
    public const string DisplayName = "HOLZ-HER Production Control";

    /// <summary>Prozessname ohne .exe (ProductionControl.exe).</summary>
    public static readonly IReadOnlyList<string> ProcessNames = ["ProductionControl"];

    /// <summary>Laufende Instanzen auf diesem Rechner (alle Benutzer).</summary>
    public static List<Process> FindRunning(IEnumerable<string>? processNames = null) =>
        (processNames ?? ProcessNames).SelectMany(Process.GetProcessesByName).ToList();

    /// <summary>
    /// Beendet die Prozesse: erst freundlich (Fenster schließen), nach <paramref name="gracePeriod"/> hart.
    /// Rückgabe: null bei Erfolg, sonst eine Fehlermeldung (z. B. fehlende Rechte bei einem anderen Benutzer).
    /// </summary>
    public static string? Stop(IReadOnlyList<Process> processes, TimeSpan gracePeriod)
    {
        foreach (var p in processes)
        {
            try { p.CloseMainWindow(); }
            catch (InvalidOperationException) { } // schon beendet
        }

        var deadline = DateTime.UtcNow + gracePeriod;
        foreach (var p in processes)
        {
            try
            {
                var remaining = deadline - DateTime.UtcNow;
                if (remaining > TimeSpan.Zero && p.WaitForExit(remaining)) continue;
                if (p.HasExited) continue;
                p.Kill(entireProcessTree: true);
                if (!p.WaitForExit(TimeSpan.FromSeconds(5)))
                    return $"{DisplayName} reagiert nicht und konnte nicht beendet werden.";
            }
            catch (InvalidOperationException)
            {
                // schon beendet
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                return $"{DisplayName} konnte nicht beendet werden ({ex.Message}). Läuft es unter einem anderen Benutzer?";
            }
        }
        return null;
    }
}
