using PConTRoL.Core.Scanning;

namespace PConTRoL.Core.Layout;

/// <summary>
/// Reine Listenlogik für Reihenfolge und Gruppen (keine Dateizugriffe).
/// "pinned" ist der laufende Auftrag auf der Maschinenseite: er bleibt immer an Position 0
/// und wird von keiner Operation bewegt.
/// </summary>
public static class LayoutOps
{
    public const string AutoGroupPrefix = "auto:";

    /// <summary>Alle betroffenen Keys in Layout-Reihenfolge (ganze Gruppen aufgelöst, ohne pinned).</summary>
    public static List<string> ExpandKeys(SideLayout layout, Selection selection, string? pinned = null)
    {
        var set = new HashSet<string>(selection.Keys, StringComparer.OrdinalIgnoreCase);
        foreach (var g in selection.WholeGroups)
            set.UnionWith(layout.MembersOf(g));
        if (pinned != null) set.Remove(pinned);
        return layout.Order.Where(set.Contains).ToList();
    }

    /// <summary>Verschiebt innerhalb einer Seite an ein Drop-Ziel. Rückgabe: true, wenn sich etwas geändert hat.</summary>
    public static bool Move(SideLayout layout, Selection selection, DropTarget target, string? pinned = null)
    {
        var keys = ExpandKeys(layout, selection, pinned);
        if (keys.Count == 0) return false;
        if (target.Anchor != null && (keys.Contains(target.Anchor, StringComparer.OrdinalIgnoreCase) || selection.WholeGroups.Contains(target.Anchor)))
            return false;

        var before = Snapshot(layout);
        var keep = KeepMembership(layout, selection, keys);
        layout.Order.RemoveAll(k => keys.Contains(k, StringComparer.OrdinalIgnoreCase));
        Insert(layout, keys, keep, [], target, pinned);
        return Snapshot(layout) != before;
    }

    /// <summary>Nimmt Aufträge aus einer Seite heraus (zum Verschieben auf die andere Seite).</summary>
    public static (Dictionary<string, string> Keep, List<GroupInfo> Carried) Extract(SideLayout layout, Selection selection, IReadOnlyList<string> keys)
    {
        var keep = KeepMembership(layout, selection, keys);
        var carried = selection.WholeGroups.Select(layout.FindGroup).Where(g => g != null).Select(g => g!.Clone()).ToList();
        layout.Order.RemoveAll(k => keys.Contains(k, StringComparer.OrdinalIgnoreCase));
        foreach (var k in keys) layout.Membership.Remove(k);
        layout.Groups.RemoveAll(g => selection.WholeGroups.Contains(g.Id));
        Cleanup(layout);
        return (keep, carried);
    }

    /// <summary>Fügt Keys an einem Ziel ein. keep: Gruppenzugehörigkeit, die erhalten bleibt (ganze Gruppen).</summary>
    public static void Insert(
        SideLayout layout,
        IReadOnlyList<string> keys,
        IReadOnlyDictionary<string, string> keep,
        IReadOnlyList<GroupInfo> carriedGroups,
        DropTarget target,
        string? pinned)
    {
        var work = layout.Order.Where(k => !string.Equals(k, pinned, StringComparison.OrdinalIgnoreCase)).ToList();
        var (index, intoGroup) = ResolveTarget(layout, work, target, pinned);

        if (intoGroup != null)
        {
            // In eine Gruppe gezogene Gruppen gehen in ihr auf.
            foreach (var g in keep.Values.Distinct().Where(g => g != intoGroup).ToList())
                layout.Groups.RemoveAll(x => x.Id == g);
            foreach (var k in keys) layout.Membership[k] = intoGroup;
        }
        else
        {
            foreach (var g in carriedGroups)
                if (layout.FindGroup(g.Id) == null) layout.Groups.Add(g.Clone());
            foreach (var k in keys)
            {
                if (keep.TryGetValue(k, out var g)) layout.Membership[k] = g;
                else layout.Membership.Remove(k);
            }
        }

        work.InsertRange(Math.Clamp(index, 0, work.Count), keys);
        layout.Order = Rebuild(layout, work, pinned);
        EnsureContiguity(layout, pinned);
        Cleanup(layout);
    }

    /// <summary>Prioritäts-Buttons: hoch/runter/ganz oben/ganz unten.</summary>
    public static bool Step(SideLayout layout, Selection selection, MoveStep step, string? pinned = null)
    {
        var keys = ExpandKeys(layout, selection, pinned);
        if (keys.Count == 0) return false;
        var selected = new HashSet<string>(keys, StringComparer.OrdinalIgnoreCase);
        var work = layout.Order.Where(k => !string.Equals(k, pinned, StringComparison.OrdinalIgnoreCase)).ToList();
        var before = string.Join("|", work);

        var groupsOfKeys = keys.Select(layout.GroupOf).Distinct().ToList();
        var container = selection.WholeGroups.Count == 0 && groupsOfKeys.Count == 1 ? groupsOfKeys[0] : null;

        if (container != null && layout.MembersOf(container).Any(k => !selected.Contains(k) && !string.Equals(k, pinned, StringComparison.OrdinalIgnoreCase)))
        {
            // Innerhalb einer Gruppe verschieben.
            var members = work.Where(k => layout.GroupOf(k) == container).ToList();
            var shifted = Shift(members, selected.Contains, step);
            var queue = new Queue<string>(shifted);
            for (var i = 0; i < work.Count; i++)
                if (layout.GroupOf(work[i]) == container) work[i] = queue.Dequeue();
        }
        else
        {
            // Oberste Ebene: Gruppen bewegen sich als Block.
            var items = TopLevelItems(layout, work);
            bool IsSel(List<string> item) => item.Any(selected.Contains);
            var shifted = Shift(items, IsSel, step);
            work = shifted.SelectMany(i => i).ToList();
        }

        layout.Order = Rebuild(layout, work, pinned);
        return string.Join("|", work) != before;
    }

    public static GroupInfo CreateEmptyGroup(SideLayout layout, string name)
    {
        var g = new GroupInfo { Id = NewGroupId(), Name = name, HadMembers = false };
        layout.Groups.Add(g);
        return g;
    }

    /// <summary>Bildet aus der Auswahl eine neue Gruppe an der Stelle des ersten markierten Auftrags.</summary>
    public static GroupInfo? GroupSelection(SideLayout layout, Selection selection, string name, string? pinned = null)
    {
        var keys = ExpandKeys(layout, selection, pinned);
        if (keys.Count == 0) return null;

        var work = layout.Order.Where(k => !string.Equals(k, pinned, StringComparison.OrdinalIgnoreCase)).ToList();
        var index = work.IndexOf(keys[0]);
        work.RemoveAll(k => keys.Contains(k, StringComparer.OrdinalIgnoreCase));
        work.InsertRange(Math.Clamp(index, 0, work.Count), keys);

        var group = new GroupInfo { Id = NewGroupId(), Name = name, HadMembers = true };
        layout.Groups.Add(group);
        foreach (var k in keys) layout.Membership[k] = group.Id;

        layout.Order = Rebuild(layout, work, pinned);
        EnsureContiguity(layout, pinned);
        Cleanup(layout);
        return group;
    }

    public static bool RenameGroup(SideLayout layout, string groupId, string name)
    {
        var g = layout.FindGroup(groupId);
        if (g == null || string.IsNullOrWhiteSpace(name)) return false;
        g.Name = name.Trim();
        g.IsAuto = false;
        return true;
    }

    /// <summary>Gruppe auflösen: Aufträge bleiben an ihrer Stelle, nur die Klammer verschwindet.</summary>
    public static bool DissolveGroup(SideLayout layout, string groupId)
    {
        var removed = layout.Groups.RemoveAll(g => g.Id == groupId) > 0;
        foreach (var k in layout.Membership.Where(m => m.Value == groupId).Select(m => m.Key).ToList())
            layout.Membership.Remove(k);
        return removed;
    }

    /// <summary>Aufträge aus ihrer Gruppe nehmen; sie landen direkt unter der Gruppe.</summary>
    public static bool RemoveFromGroup(SideLayout layout, IEnumerable<string> keys, string? pinned = null)
    {
        var leaving = keys.Where(k => layout.GroupOf(k) != null).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (leaving.Count == 0) return false;
        var work = layout.Order.Where(k => !string.Equals(k, pinned, StringComparison.OrdinalIgnoreCase)).ToList();

        foreach (var groupId in leaving.Select(layout.GroupOf).Distinct().ToList())
        {
            var block = work.Where(k => layout.GroupOf(k) == groupId).ToList();
            if (block.Count == 0) continue;
            var start = work.IndexOf(block[0]);
            work.RemoveAll(block.Contains);
            var reordered = block.Where(k => !leaving.Contains(k)).Concat(block.Where(leaving.Contains)).ToList();
            work.InsertRange(start, reordered);
        }
        foreach (var k in leaving) layout.Membership.Remove(k);

        layout.Order = Rebuild(layout, work, pinned);
        Cleanup(layout);
        return true;
    }

    /// <summary>
    /// Gleicht das gespeicherte Layout mit den tatsächlich vorhandenen Dateien ab.
    /// Vorrat: neue Aufträge kommen als Gruppe je Nest-Job ans Ende (oder an ihre bestehende Gruppe).
    /// Maschine: Reihenfolge = Änderungsdatum, Gruppen nur als Zuordnung.
    /// </summary>
    public static void Reconcile(LayoutDocument doc, ScanResult scan)
    {
        ReconcileStock(doc.Stock, scan.Stock);

        var machine = doc.Machine;
        machine.Order = scan.Machine.Select(j => j.Key).ToList();
        PruneMembership(machine);
        Cleanup(machine);
    }

    private static void ReconcileStock(SideLayout layout, IReadOnlyList<JobInfo> jobs)
    {
        var present = jobs.Select(j => j.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        layout.Order = layout.Order.Where(k => present.Contains(k) && seen.Add(k)).ToList();
        PruneMembership(layout);

        var newJobs = jobs.Where(j => !seen.Contains(j.Key)).ToList();
        if (newJobs.Count > 0)
        {
            var batches = newJobs
                .GroupBy(j => j.NestJob, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(g => g.Max(j => j.Priority ?? int.MinValue))
                .ThenBy(g => g.Min(j => j.LastWriteUtc))
                .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase);

            foreach (var batch in batches)
            {
                var keys = batch
                    .OrderBy(j => j.BoardId ?? int.MaxValue)
                    .ThenBy(j => j.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(j => j.Key)
                    .ToList();
                var groupId = AutoGroupPrefix + batch.Key;
                var group = layout.FindGroup(groupId);
                if (group == null)
                {
                    group = new GroupInfo { Id = groupId, Name = batch.Key, IsAuto = true };
                    layout.Groups.Add(group);
                }
                group.HadMembers = true;

                var lastMember = layout.Order.FindLastIndex(k => layout.GroupOf(k) == groupId);
                layout.Order.InsertRange(lastMember >= 0 ? lastMember + 1 : layout.Order.Count, keys);
                foreach (var k in keys) layout.Membership[k] = groupId;
            }
        }

        EnsureContiguity(layout, null);
        Cleanup(layout);
    }

    /// <summary>Holt verstreute Gruppenmitglieder an die Stelle des ersten Mitglieds.</summary>
    public static void EnsureContiguity(SideLayout layout, string? pinned)
    {
        var work = layout.Order.Where(k => !string.Equals(k, pinned, StringComparison.OrdinalIgnoreCase)).ToList();
        var result = new List<string>(work.Count);
        var done = new HashSet<string>();
        foreach (var k in work)
        {
            var g = layout.GroupOf(k);
            if (g == null) { result.Add(k); continue; }
            if (!done.Add(g)) continue;
            result.AddRange(work.Where(x => layout.GroupOf(x) == g));
        }
        layout.Order = Rebuild(layout, result, pinned);
    }

    private static void PruneMembership(SideLayout layout)
    {
        var present = layout.Order.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var groupIds = layout.Groups.Select(g => g.Id).ToHashSet();
        foreach (var k in layout.Membership.Keys.ToList())
            if (!present.Contains(k) || !groupIds.Contains(layout.Membership[k]))
                layout.Membership.Remove(k);
    }

    /// <summary>Leere Gruppen entfernen, sofern sie schon einmal Mitglieder hatten.</summary>
    private static void Cleanup(SideLayout layout)
    {
        var used = layout.Membership.Values.ToHashSet();
        foreach (var g in layout.Groups.Where(g => used.Contains(g.Id))) g.HadMembers = true;
        layout.Groups.RemoveAll(g => !used.Contains(g.Id) && (g.HadMembers || g.IsAuto));
    }

    private static Dictionary<string, string> KeepMembership(SideLayout layout, Selection selection, IReadOnlyList<string> keys)
    {
        var keep = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var k in keys)
            if (layout.GroupOf(k) is { } g && selection.WholeGroups.Contains(g))
                keep[k] = g;
        return keep;
    }

    private static (int Index, string? IntoGroup) ResolveTarget(SideLayout layout, List<string> work, DropTarget target, string? pinned)
    {
        var anchor = target.Anchor;
        if (anchor != null && string.Equals(anchor, pinned, StringComparison.OrdinalIgnoreCase))
            return (0, null); // Vor/nach dem laufenden Auftrag = direkt dahinter.

        int First(string g) => work.FindIndex(k => layout.GroupOf(k) == g);
        int Last(string g) => work.FindLastIndex(k => layout.GroupOf(k) == g);
        string? ExistingGroup(string? g) => g != null && layout.FindGroup(g) != null ? g : null;

        switch (target.Kind)
        {
            case DropKind.BeforeJob:
            {
                var i = work.FindIndex(k => string.Equals(k, anchor, StringComparison.OrdinalIgnoreCase));
                return i < 0 ? (work.Count, null) : (i, layout.GroupOf(anchor!));
            }
            case DropKind.AfterJob:
            {
                var i = work.FindIndex(k => string.Equals(k, anchor, StringComparison.OrdinalIgnoreCase));
                return i < 0 ? (work.Count, null) : (i + 1, layout.GroupOf(anchor!));
            }
            case DropKind.BeforeGroup:
            {
                var i = anchor == null ? -1 : First(anchor);
                return (i < 0 ? work.Count : i, null);
            }
            case DropKind.AfterGroup:
            {
                var i = anchor == null ? -1 : Last(anchor);
                return (i < 0 ? work.Count : i + 1, null);
            }
            case DropKind.IntoGroupStart:
            {
                var g = ExistingGroup(anchor);
                var i = g == null ? -1 : First(g);
                return (i < 0 ? work.Count : i, g);
            }
            case DropKind.IntoGroupEnd:
            {
                var g = ExistingGroup(anchor);
                var i = g == null ? -1 : Last(g);
                return (i < 0 ? work.Count : i + 1, g);
            }
            case DropKind.Start:
                return (0, null);
            default:
                return (work.Count, null);
        }
    }

    private static List<List<string>> TopLevelItems(SideLayout layout, List<string> work)
    {
        var items = new List<List<string>>();
        string? currentGroup = null;
        foreach (var k in work)
        {
            var g = layout.GroupOf(k);
            if (g != null && g == currentGroup)
            {
                items[^1].Add(k);
                continue;
            }
            items.Add([k]);
            currentGroup = g;
        }
        return items;
    }

    internal static List<T> Shift<T>(List<T> list, Func<T, bool> isSelected, MoveStep step)
    {
        var result = new List<T>(list);
        switch (step)
        {
            case MoveStep.Top:
                return result.Where(isSelected).Concat(result.Where(x => !isSelected(x))).ToList();
            case MoveStep.Bottom:
                return result.Where(x => !isSelected(x)).Concat(result.Where(isSelected)).ToList();
            case MoveStep.Up:
                for (var i = 1; i < result.Count; i++)
                    if (isSelected(result[i]) && !isSelected(result[i - 1]))
                        (result[i], result[i - 1]) = (result[i - 1], result[i]);
                return result;
            default:
                for (var i = result.Count - 2; i >= 0; i--)
                    if (isSelected(result[i]) && !isSelected(result[i + 1]))
                        (result[i], result[i + 1]) = (result[i + 1], result[i]);
                return result;
        }
    }

    private static List<string> Rebuild(SideLayout layout, List<string> work, string? pinned) =>
        pinned != null && layout.Order.Contains(pinned, StringComparer.OrdinalIgnoreCase) ? [pinned, .. work] : work;

    private static string Snapshot(SideLayout layout) =>
        string.Join("|", layout.Order.Select(k => k + "=" + layout.GroupOf(k)));

    private static string NewGroupId() => "g:" + Guid.NewGuid().ToString("N")[..10];
}
