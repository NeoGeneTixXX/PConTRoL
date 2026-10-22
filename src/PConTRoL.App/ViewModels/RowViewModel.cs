using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using PConTRoL.Core;
using PConTRoL.Core.Layout;

namespace PConTRoL.App.ViewModels;

public enum DropIndicator
{
    None,
    Above,
    Below,
    Into,
}

/// <summary>Eine Zeile in einer der beiden Listen (Auftrag oder Gruppenkopf).</summary>
public sealed class RowViewModel : INotifyPropertyChanged
{
    private static readonly CultureInfo De = CultureInfo.GetCultureInfo("de-DE");
    private DropIndicator _drop;

    private RowViewModel(LayoutRow row) => Row = row;

    public LayoutRow Row { get; }
    public JobInfo? Job { get; private init; }
    public IReadOnlyList<JobInfo> Members { get; private init; } = [];

    public string Id => Row.Id;
    public bool IsGroup => Row.Kind == RowKind.Group;
    public bool IsPinned => Row.IsPinned;
    public string? GroupId => Row.GroupId;
    public double Indent => Row.Depth * 26;
    public bool IsCollapsed { get; private init; }

    public string Glyph => IsGroup ? (IsCollapsed ? "▶" : "▼") : IsPinned ? "⚙" : "";
    public string PositionText => Row.Position?.ToString() ?? "";
    public string Name { get; private init; } = "";
    public string NestJob { get; private init; } = "";
    public string Board { get; private init; } = "";
    public string Material { get; private init; } = "";
    public string Dims { get; private init; } = "";
    public string Parts { get; private init; } = "";
    public string Efficiency { get; private init; } = "";
    public string Priority { get; private init; } = "";
    public string ProductionNumber { get; private init; } = "";
    public string Modified { get; private init; } = "";
    public string ToolTip { get; private init; } = "";

    /// <summary>Alles, was sich in der Anzeige ändern kann – zum Erkennen, ob neu gezeichnet werden muss.</summary>
    public string Signature { get; private init; } = "";

    public DropIndicator Drop
    {
        get => _drop;
        set
        {
            if (_drop == value) return;
            _drop = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public static RowViewModel ForJob(LayoutRow row, JobInfo job)
    {
        var modified = job.LastWriteUtc.ToLocalTime().ToString("dd.MM. HH:mm:ss", De);
        return new RowViewModel(row)
        {
            Job = job,
            Name = row.IsPinned ? job.Key + "   (läuft)" : job.Key,
            NestJob = job.NestJob,
            Board = job.BoardText,
            Material = job.Material,
            Dims = job.DimsText,
            Parts = job.PartsOnBoard?.ToString() ?? (job.Parts.Count > 0 ? job.Parts.Count.ToString() : ""),
            Efficiency = job.Efficiency is { } e ? e.ToString("0.0", De) + " %" : "",
            Priority = job.Priority?.ToString() ?? "",
            ProductionNumber = job.ProductionNumber,
            Modified = modified,
            ToolTip = row.IsPinned
                ? "Wird gerade an der Maschine bearbeitet – wird nicht verschoben."
                : $"{job.Key}\n{job.NestJob}",
            Signature = $"J|{row.Id}|{row.GroupId}|{row.Depth}|{row.Position}|{row.IsPinned}|{job.LastWriteUtc.Ticks}|{job.Material}|{job.BoardText}",
        };
    }

    public static RowViewModel ForGroup(LayoutRow row, GroupInfo? group, IReadOnlyList<JobInfo> members, bool collapsed)
    {
        var materials = members.Select(m => m.Material).Where(m => m.Length > 0).Distinct().ToList();
        var dims = members.Select(m => m.DimsText).Where(d => d.Length > 0).Distinct().ToList();
        var nestJobs = members.Select(m => m.NestJob).Distinct().ToList();
        var parts = members.Sum(m => m.PartsOnBoard ?? m.Parts.Count);
        var prio = members.Where(m => m.Priority != null).Select(m => m.Priority!.Value).DefaultIfEmpty().Max();
        var first = members.Count > 0 ? members[0].Key : "";
        var name = group?.Name ?? row.Id;
        var count = members.Count == 1 ? "1 Platte" : $"{members.Count} Platten";

        return new RowViewModel(row)
        {
            Members = members,
            IsCollapsed = collapsed,
            Name = $"{name}   ({count})",
            NestJob = nestJobs.Count == 1 ? nestJobs[0] : nestJobs.Count > 1 ? "gemischt" : "",
            Board = members.Count.ToString(),
            Material = materials.Count == 1 ? materials[0] : materials.Count > 1 ? "gemischt" : "",
            Dims = dims.Count == 1 ? dims[0] : dims.Count > 1 ? "gemischt" : "",
            Parts = members.Count > 0 ? parts.ToString() : "",
            Priority = members.Any(m => m.Priority != null) ? prio.ToString() : "",
            ToolTip = members.Count == 0
                ? "Leere Gruppe – Aufträge hierher ziehen."
                : $"Gruppe „{name}“ – {count}. Doppelklick oder ▶/▼ zum Auf-/Zuklappen.",
            Signature = $"G|{row.Id}|{name}|{collapsed}|{string.Join(",", row.Members)}|{first}",
        };
    }
}
