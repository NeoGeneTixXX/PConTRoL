namespace PConTRoL.Core.Layout;

public sealed class GroupInfo
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";

    /// <summary>Automatisch angelegte Gruppe für einen Nest-Job (Unterordner).</summary>
    public bool IsAuto { get; set; }

    /// <summary>Leere Gruppen bleiben nur, solange sie noch nie Mitglieder hatten (frisch angelegt).</summary>
    public bool HadMembers { get; set; }

    public GroupInfo Clone() => new() { Id = Id, Name = Name, IsAuto = IsAuto, HadMembers = HadMembers };
}

/// <summary>
/// Reihenfolge und Gruppen einer Seite. Order ist die flache Reihenfolge der Auftrags-Keys,
/// Membership ordnet Keys einer Gruppe zu. Mitglieder einer Gruppe stehen in Order zusammenhängend.
/// Auf der Maschinenseite wird Order nicht gespeichert, sondern aus den Änderungsdaten gebildet.
/// </summary>
public sealed class SideLayout
{
    public List<string> Order { get; set; } = [];
    public List<GroupInfo> Groups { get; set; } = [];
    public Dictionary<string, string> Membership { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public GroupInfo? FindGroup(string id) => Groups.FirstOrDefault(g => g.Id == id);

    public string? GroupOf(string key) => Membership.TryGetValue(key, out var g) ? g : null;

    public List<string> MembersOf(string groupId) => Order.Where(k => GroupOf(k) == groupId).ToList();

    public SideLayout Clone() => new()
    {
        Order = [.. Order],
        Groups = Groups.Select(g => g.Clone()).ToList(),
        Membership = new Dictionary<string, string>(Membership, StringComparer.OrdinalIgnoreCase),
    };
}

public sealed class LayoutDocument
{
    public int Version { get; set; } = 1;
    public SideLayout Stock { get; set; } = new();
    public SideLayout Machine { get; set; } = new();

    public SideLayout For(Side side) => side == Side.Stock ? Stock : Machine;

    public LayoutDocument Clone() => new() { Version = Version, Stock = Stock.Clone(), Machine = Machine.Clone() };
}

/// <summary>Was gezogen bzw. markiert wurde: einzelne Aufträge und ganze Gruppen.</summary>
public sealed class Selection
{
    public List<string> Keys { get; init; } = [];
    public HashSet<string> WholeGroups { get; init; } = [];

    public bool IsEmpty => Keys.Count == 0 && WholeGroups.Count == 0;

    public static Selection OfKeys(params string[] keys) => new() { Keys = [.. keys] };

    public static Selection OfGroup(string groupId) => new() { WholeGroups = [groupId] };
}

public enum DropKind
{
    BeforeJob,
    AfterJob,
    BeforeGroup,
    AfterGroup,
    IntoGroupStart,
    IntoGroupEnd,

    /// <summary>Ganz oben (auf der Maschinenseite: direkt nach dem laufenden Auftrag).</summary>
    Start,

    /// <summary>Ganz unten.</summary>
    End,
}

public sealed record DropTarget(DropKind Kind, string? Anchor = null)
{
    public static readonly DropTarget Start = new(DropKind.Start);
    public static readonly DropTarget End = new(DropKind.End);
}

public enum MoveStep
{
    Up,
    Down,
    Top,
    Bottom,
}
