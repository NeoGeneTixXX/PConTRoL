using PConTRoL.Core.Scanning;

namespace PConTRoL.Core.Timing;

/// <summary>
/// Misst, wie lange der oberste Auftrag in _Input (= läuft gerade) braucht.
/// Die Uhr startet, sobald ein Auftrag nach oben rückt – weil der vorige fertig ist oder die Maschine leer war.
/// Fertig ist er, wenn <see cref="Statistics.FinishedJobTracker"/> ihn als fertig meldet.
/// Wann der Auftrag beim Programmstart angefangen hat, ist unbekannt: Der wird nicht gemessen.
/// </summary>
public sealed class RunTimeTracker
{
    private bool _initialized;
    private string? _runningKey;
    private DateTime? _sinceUtc;

    /// <summary>Seit wann der oberste Auftrag läuft (null = unbekannt oder Maschine leer).</summary>
    public DateTime? RunningSinceUtc => _runningKey != null ? _sinceUtc : null;

    /// <summary>Liefert die Laufzeiten der Aufträge, die seit dem letzten Aufruf fertig geworden sind.</summary>
    public IReadOnlyList<RunTimeRecord> Update(ScanResult scan, IReadOnlyList<JobInfo> finished, DateTime nowUtc)
    {
        var top = scan.RunningKey;
        if (!_initialized)
        {
            _initialized = true;
            _runningKey = top;
            _sinceUtc = null;
            return [];
        }
        if (string.Equals(top, _runningKey, StringComparison.OrdinalIgnoreCase)) return [];

        var result = new List<RunTimeRecord>();
        var previousFinished = _runningKey != null
            ? finished.FirstOrDefault(j => string.Equals(j.Key, _runningKey, StringComparison.OrdinalIgnoreCase))
            : null;
        if (previousFinished != null && _sinceUtc is { } since && RunTimeRecord.FromJob(previousFinished, since, nowUtc) is { } record)
            result.Add(record);

        // Neuer oberster Auftrag: Er startet jetzt, wenn der vorige fertig wurde oder die Maschine leer war.
        // Rückt er aus einem anderen Grund nach oben (vorigen zurückgeholt/gelöscht), ist der Start unklar.
        var startsNow = _runningKey == null || previousFinished != null;
        _runningKey = top;
        _sinceUtc = top != null && startsNow ? nowUtc : null;
        return result;
    }
}
