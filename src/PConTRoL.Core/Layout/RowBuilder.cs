namespace PConTRoL.Core.Layout;

public enum RowKind
{
    Job,
    Group,
}

/// <summary>Eine Zeile in der Anzeige: Gruppenkopf oder Auftrag.</summary>
public sealed record LayoutRow(
    RowKind Kind,
    string Id,
    string? GroupId,
    int Depth,
    int? Position,
    bool IsPinned,
    IReadOnlyList<string> Members);

/// <summary>
/// Baut aus Reihenfolge + Gruppen die Zeilen für die Liste. Aufeinanderfolgende Mitglieder einer Gruppe
/// bilden einen Block mit Kopfzeile. Liegen Mitglieder (z. B. durch Änderungen von außen) nicht zusammen,
/// erscheint die Gruppe mehrfach – die Anzeige zeigt immer die echte Reihenfolge.
/// </summary>
public static class RowBuilder
{
    public static List<LayoutRow> Build(SideLayout layout, string? pinned, ISet<string> collapsedGroups)
    {
        var rows = new List<LayoutRow>();
        var order = layout.Order;
        var position = 0;
        var i = 0;

        while (i < order.Count)
        {
            var key = order[i];
            if (string.Equals(key, pinned, StringComparison.OrdinalIgnoreCase))
            {
                rows.Add(new LayoutRow(RowKind.Job, key, null, 0, ++position, true, []));
                i++;
                continue;
            }

            var group = layout.GroupOf(key);
            if (group == null)
            {
                rows.Add(new LayoutRow(RowKind.Job, key, null, 0, ++position, false, []));
                i++;
                continue;
            }

            var run = new List<string>();
            while (i < order.Count && layout.GroupOf(order[i]) == group && !string.Equals(order[i], pinned, StringComparison.OrdinalIgnoreCase))
                run.Add(order[i++]);

            rows.Add(new LayoutRow(RowKind.Group, group, group, 0, null, false, run));
            var collapsed = collapsedGroups.Contains(group);
            foreach (var member in run)
            {
                position++;
                if (!collapsed)
                    rows.Add(new LayoutRow(RowKind.Job, member, group, 1, position, false, []));
            }
        }

        var used = layout.Membership.Values.ToHashSet();
        foreach (var empty in layout.Groups.Where(g => !used.Contains(g.Id)))
            rows.Add(new LayoutRow(RowKind.Group, empty.Id, empty.Id, 0, null, false, []));

        return rows;
    }
}
