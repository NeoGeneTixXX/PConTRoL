using System.Collections.ObjectModel;
using PConTRoL.Core;
using PConTRoL.Core.Statistics;
using PConTRoL.Core.Timing;

namespace PConTRoL.App.ViewModels;

/// <summary>Eine Zeile der Zeitleiste: Uhrzeit, Name, Dauer/Restzeit.</summary>
public sealed record TimelineEntry(string Time, string Name, string Info, string ToolTip, bool IsRunning, bool IsLast)
{
    public bool HasLine => !IsLast;
}

/// <summary>Was das Tray-Pop-up anzeigt: Zeitleiste mit laufendem Auftrag und den nächsten <see cref="NextCount"/>.</summary>
public sealed class QueueStatus : Observable
{
    public const int NextCount = 5;

    /// <summary>Lange Namen vorne kürzen: Das Ende (Nummer der Platte) unterscheidet die Aufträge.</summary>
    private const int MaxNameLength = 30;

    private string _currentName = "–";
    private string _headerInfo = "";
    private string _footer = "";
    private string _message = "";
    private bool _hasCurrent;
    private string _signature = "";

    public string CurrentName { get => _currentName; private set => Set(ref _currentName, value); }
    public bool HasCurrent { get => _hasCurrent; private set => Set(ref _hasCurrent, value); }

    /// <summary>Rechts oben, z. B. „leer ca. 12:10“.</summary>
    public string HeaderInfo { get => _headerInfo; private set => Set(ref _headerInfo, value); }

    /// <summary>Statt der Zeitleiste (Fehler, keine Aufträge).</summary>
    public string Message { get => _message; private set => Set(ref _message, value); }

    public string Footer { get => _footer; private set => Set(ref _footer, value); }
    public ObservableCollection<TimelineEntry> Entries { get; } = [];

    public void Update(IReadOnlyList<JobInfo> machine, string? error, IReadOnlyList<ForecastEntry>? forecast = null)
    {
        if (error != null)
        {
            Apply(false, "Keine Verbindung", "", error, [], "");
            return;
        }
        if (machine.Count == 0)
        {
            Apply(false, "Keine Aufträge an der Maschine", "", "Keine Aufträge an der Maschine", [], "");
            return;
        }

        forecast ??= machine.Select(j => new ForecastEntry(j, null, null, null)).ToList();
        var shown = forecast.Take(NextCount + 1).ToList();
        var entries = shown.Select((f, i) => Describe(f, i, i > 0 ? shown[i - 1].Job : null, i == shown.Count - 1)).ToList();

        var end = forecast[^1].EndUtc;
        var header = end != null ? "leer ca. " + Clock(end.Value) : "";
        var rest = forecast.Count - shown.Count;
        var footer = rest > 0 ? $"+ {rest} weitere" + (end != null ? " bis " + Clock(end.Value) : "") : "";
        Apply(true, machine[0].Key, header, "", entries, footer);
    }

    private void Apply(bool hasCurrent, string current, string header, string message, List<TimelineEntry> entries, string footer)
    {
        var signature = string.Join("|", entries) + "|" + header + "|" + message + "|" + footer + "|" + current;
        if (signature == _signature) return;
        _signature = signature;

        HasCurrent = hasCurrent;
        CurrentName = current;
        HeaderInfo = header;
        Message = message;
        Footer = footer;
        Entries.Clear();
        foreach (var e in entries) Entries.Add(e);
    }

    private static TimelineEntry Describe(ForecastEntry f, int index, JobInfo? previous, bool isLast)
    {
        var job = f.Job;
        var parts = new List<string>();
        if (index == 0)
        {
            parts.Add(f.Remaining switch
            {
                { TotalSeconds: < 60 } => "läuft · gleich fertig",
                { } r => $"läuft · noch {Minutes(r)}",
                null when f.Estimate != null => $"läuft · ca. {Minutes(f.Estimate.Duration)}",
                _ => "läuft",
            });
        }
        else
            parts.Add(f.Estimate != null ? Minutes(f.Estimate.Duration) : "Dauer noch unbekannt");

        // Material nur nennen, wenn es wechselt (beim ersten immer): Dann muss eine andere Platte aufgelegt werden.
        var material = MaterialName.Normalize(job.Material);
        if (material.Length > 0 && (previous == null || MaterialName.Key(previous.Material) != MaterialName.Key(material)))
            parts.Add(material);

        var time = index == 0
            ? (f.StartUtc != null ? Clock(f.StartUtc.Value) : "jetzt")
            : (f.StartUtc != null ? Clock(f.StartUtc.Value) : "");

        return new TimelineEntry(time, Shorten(job.Key), string.Join(" · ", parts), ToolTipFor(f), index == 0, isLast);
    }

    private static string ToolTipFor(ForecastEntry f)
    {
        var job = f.Job;
        var lines = new List<string> { job.Key };
        var info = string.Join("  ·  ", new[]
        {
            job.Material, job.DimsText, RunTimeRecord.PartsOf(job) is > 0 and var p ? $"{p} Teile" : "",
            job.BoardText.Length > 0 ? "Platte " + job.BoardText : "",
        }.Where(s => s.Length > 0));
        if (info.Length > 0) lines.Add(info);
        if (f.Estimate is { } e)
            lines.Add($"Geschätzt {Minutes(e.Duration)} aus {e.Samples} {(e.Samples == 1 ? "Messung" : "Messungen")} ({Basis(e.Basis)})");
        else
            lines.Add("Noch keine Messungen – die Zeit wird geschätzt, sobald Platten fertig werden.");
        return string.Join(Environment.NewLine, lines);
    }

    private static string Basis(EstimateBasis basis) => basis switch
    {
        EstimateBasis.Exact => "gleiches Material, Maß und Teilezahl",
        EstimateBasis.SameBoard => "gleiches Material und Maß",
        EstimateBasis.SameMaterial => "gleiches Material",
        _ => "alle Platten",
    };

    internal static string Shorten(string name) =>
        name.Length <= MaxNameLength ? name : "…" + name[^(MaxNameLength - 1)..];

    internal static string Minutes(TimeSpan t)
    {
        var minutes = (int)Math.Round(t.TotalMinutes);
        if (minutes < 1) return "< 1 min";
        return minutes < 60 ? $"{minutes} min" : $"{minutes / 60} h {minutes % 60:00} min";
    }

    private static string Clock(DateTime utc) => utc.ToLocalTime().ToString("H:mm");
}
