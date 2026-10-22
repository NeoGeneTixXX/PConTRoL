using System.Collections.ObjectModel;
using PConTRoL.Core;

namespace PConTRoL.App.ViewModels;

public sealed record QueueEntry(int Position, string Name, string Info);

/// <summary>Was das Tray-Pop-up anzeigt: laufender Auftrag + die nächsten 10.</summary>
public sealed class QueueStatus : Observable
{
    public const int NextCount = 10;

    private string _currentName = "–";
    private string _currentInfo = "";
    private string _footer = "";
    private bool _hasCurrent;
    private string _signature = "";

    public string CurrentName { get => _currentName; private set => Set(ref _currentName, value); }
    public string CurrentInfo { get => _currentInfo; private set => Set(ref _currentInfo, value); }
    public bool HasCurrent { get => _hasCurrent; private set => Set(ref _hasCurrent, value); }
    public string Footer { get => _footer; private set => Set(ref _footer, value); }
    public ObservableCollection<QueueEntry> Next { get; } = [];

    public void Update(IReadOnlyList<JobInfo> machine, string? error)
    {
        var signature = error + "|" + string.Join("|", machine.Take(NextCount + 1).Select(j => j.Key)) + "|" + machine.Count;
        if (signature == _signature) return;
        _signature = signature;

        if (error != null)
        {
            HasCurrent = false;
            CurrentName = "Keine Verbindung";
            CurrentInfo = error;
            Next.Clear();
            Footer = "";
            return;
        }

        HasCurrent = machine.Count > 0;
        CurrentName = machine.Count > 0 ? machine[0].Key : "Keine Aufträge an der Maschine";
        CurrentInfo = machine.Count > 0 ? Describe(machine[0]) : "";

        Next.Clear();
        foreach (var (job, i) in machine.Skip(1).Take(NextCount).Select((j, i) => (j, i)))
            Next.Add(new QueueEntry(i + 2, job.Key, Describe(job)));

        var rest = machine.Count - 1 - NextCount;
        Footer = rest > 0 ? $"… und {rest} weitere" : "";
    }

    private static string Describe(JobInfo job) =>
        string.Join("  ·  ", new[] { job.Material, job.DimsText, job.BoardText.Length > 0 ? "Platte " + job.BoardText : "" }
            .Where(s => s.Length > 0));
}
