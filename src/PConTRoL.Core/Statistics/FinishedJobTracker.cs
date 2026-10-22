using PConTRoL.Core.Scanning;

namespace PConTRoL.Core.Statistics;

/// <summary>
/// Erkennt Platten, die die Maschine fertig bearbeitet hat: Sie lagen beim letzten Einlesen in _Input und sind
/// jetzt weg, ohne im Vorrat aufzutauchen (dann hätte PConTRoL sie zurückgeholt). Die Maschine kopiert fertige
/// Aufträge ins Archiv und löscht sie aus _Input.
/// </summary>
public sealed class FinishedJobTracker
{
    private readonly Func<string, bool> _fileExists;
    private Dictionary<string, JobInfo>? _lastMachine;

    public FinishedJobTracker(Func<string, bool>? fileExists = null) => _fileExists = fileExists ?? File.Exists;

    /// <summary>Liefert die seit dem letzten Aufruf fertig gewordenen Platten (beim ersten Aufruf keine).</summary>
    public IReadOnlyList<JobInfo> Update(ScanResult scan)
    {
        var machine = new Dictionary<string, JobInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var job in scan.Machine) machine.TryAdd(job.Key, job);

        var previous = _lastMachine;
        _lastMachine = machine;
        if (previous == null) return [];

        var stock = new HashSet<string>(scan.Stock.Select(j => j.Key), StringComparer.OrdinalIgnoreCase);
        return previous.Values
            .Where(j => !machine.ContainsKey(j.Key) && !stock.Contains(j.Key))
            // Datei kurz gesperrt/nicht lesbar fehlt im Scan, liegt aber noch da – das ist nicht "fertig".
            .Where(j => !_fileExists(j.HopPath))
            .ToList();
    }
}
