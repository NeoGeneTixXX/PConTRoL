using PConTRoL.Core.Layout;

namespace PConTRoL.Tests;

public class LayoutOpsTests
{
    /// <summary>a b [G: g1 g2 g3] c</summary>
    private static SideLayout Sample()
    {
        var l = new SideLayout { Order = ["a", "b", "g1", "g2", "g3", "c"] };
        l.Groups.Add(new GroupInfo { Id = "G", Name = "Gruppe", HadMembers = true });
        foreach (var k in new[] { "g1", "g2", "g3" }) l.Membership[k] = "G";
        return l;
    }

    [Fact]
    public void Move_job_before_another()
    {
        var l = Sample();
        Assert.True(LayoutOps.Move(l, Selection.OfKeys("c"), new DropTarget(DropKind.BeforeJob, "a")));
        Assert.Equal(["c", "a", "b", "g1", "g2", "g3"], l.Order);
        Assert.Null(l.GroupOf("c"));
    }

    [Fact]
    public void Dropping_on_job_inside_group_joins_the_group()
    {
        var l = Sample();
        LayoutOps.Move(l, Selection.OfKeys("a"), new DropTarget(DropKind.AfterJob, "g1"));
        Assert.Equal(["b", "g1", "a", "g2", "g3", "c"], l.Order);
        Assert.Equal("G", l.GroupOf("a"));
    }

    [Fact]
    public void Moving_whole_group_keeps_it_together()
    {
        var l = Sample();
        LayoutOps.Move(l, Selection.OfGroup("G"), DropTarget.Start);
        Assert.Equal(["g1", "g2", "g3", "a", "b", "c"], l.Order);
        Assert.Equal("G", l.GroupOf("g2"));
    }

    [Fact]
    public void Group_dropped_into_other_group_merges()
    {
        var l = Sample();
        LayoutOps.GroupSelection(l, Selection.OfKeys("a", "b"), "Neu");
        var newId = l.GroupOf("a")!;
        LayoutOps.Move(l, Selection.OfGroup(newId), new DropTarget(DropKind.IntoGroupEnd, "G"));
        Assert.Equal(["g1", "g2", "g3", "a", "b", "c"], l.Order);
        Assert.All(new[] { "a", "b", "g1" }, k => Assert.Equal("G", l.GroupOf(k)));
        Assert.Null(l.FindGroup(newId));
    }

    [Fact]
    public void Group_selection_collects_scattered_jobs_at_first_position()
    {
        var l = Sample();
        var g = LayoutOps.GroupSelection(l, Selection.OfKeys("b", "c"), "BC")!;
        Assert.Equal(["a", "b", "c", "g1", "g2", "g3"], l.Order);
        Assert.Equal(g.Id, l.GroupOf("c"));
    }

    [Fact]
    public void Step_up_inside_group_stays_in_group()
    {
        var l = Sample();
        LayoutOps.Step(l, Selection.OfKeys("g3"), MoveStep.Up);
        Assert.Equal(["a", "b", "g1", "g3", "g2", "c"], l.Order);
        LayoutOps.Step(l, Selection.OfKeys("g3"), MoveStep.Top);
        Assert.Equal(["a", "b", "g3", "g1", "g2", "c"], l.Order);
    }

    [Fact]
    public void Jobs_can_be_reordered_inside_their_group_by_dragging()
    {
        var l = Sample();
        Assert.True(LayoutOps.Move(l, Selection.OfKeys("g3"), new DropTarget(DropKind.BeforeJob, "g1")));
        Assert.Equal(["a", "b", "g3", "g1", "g2", "c"], l.Order);
        Assert.Equal("G", l.GroupOf("g3"));
    }

    [Fact]
    public void Sort_group_by_name_is_natural_and_keeps_group_in_place()
    {
        var l = new SideLayout { Order = ["a", "Platte_10", "platte_2", "Platte_1", "z"] };
        l.Groups.Add(new GroupInfo { Id = "G", Name = "Gruppe", HadMembers = true });
        foreach (var k in new[] { "Platte_10", "platte_2", "Platte_1" }) l.Membership[k] = "G";

        Assert.True(LayoutOps.SortGroupByName(l, "G"));
        Assert.Equal(["a", "Platte_1", "platte_2", "Platte_10", "z"], l.Order);
        Assert.False(LayoutOps.SortGroupByName(l, "G"));
    }

    [Fact]
    public void Step_on_top_level_moves_group_as_block()
    {
        var l = Sample();
        LayoutOps.Step(l, Selection.OfKeys("c"), MoveStep.Up);
        Assert.Equal(["a", "b", "c", "g1", "g2", "g3"], l.Order);
        LayoutOps.Step(l, Selection.OfGroup("G"), MoveStep.Top);
        Assert.Equal(["g1", "g2", "g3", "a", "b", "c"], l.Order);
    }

    [Fact]
    public void Pinned_job_never_moves()
    {
        var l = new SideLayout { Order = ["run", "a", "b"] };
        LayoutOps.Move(l, Selection.OfKeys("b"), new DropTarget(DropKind.BeforeJob, "run"), pinned: "run");
        Assert.Equal(["run", "b", "a"], l.Order);
        LayoutOps.Step(l, Selection.OfKeys("a"), MoveStep.Top, pinned: "run");
        Assert.Equal(["run", "a", "b"], l.Order);
        Assert.False(LayoutOps.Move(l, Selection.OfKeys("run"), DropTarget.End, pinned: "run"));
        Assert.Equal("run", l.Order[0]);
    }

    [Fact]
    public void Remove_from_group_places_job_below_group()
    {
        var l = Sample();
        LayoutOps.RemoveFromGroup(l, ["g1"]);
        Assert.Equal(["a", "b", "g2", "g3", "g1", "c"], l.Order);
        Assert.Null(l.GroupOf("g1"));
    }

    [Fact]
    public void Dissolve_keeps_order_and_empty_new_group_survives_until_used()
    {
        var l = Sample();
        LayoutOps.DissolveGroup(l, "G");
        Assert.Equal(["a", "b", "g1", "g2", "g3", "c"], l.Order);
        Assert.Empty(l.Membership);

        var empty = LayoutOps.CreateEmptyGroup(l, "Leer");
        LayoutOps.Move(l, Selection.OfKeys("a"), new DropTarget(DropKind.IntoGroupEnd, empty.Id)); // a → Ende, in Gruppe
        Assert.Equal(empty.Id, l.GroupOf("a"));
        LayoutOps.Move(l, Selection.OfKeys("a"), DropTarget.Start); // Gruppe wird leer → verschwindet
        Assert.Null(l.FindGroup(empty.Id));
    }

    [Fact]
    public void Rows_show_group_headers_and_hide_collapsed_members()
    {
        var l = Sample();
        var rows = RowBuilder.Build(l, null, new HashSet<string> { "G" });
        Assert.Equal(["a", "b", "G", "c"], rows.Select(r => r.Id));
        Assert.Equal(6, rows.Last().Position); // Nummer zählt eingeklappte Mitglieder mit
        Assert.Equal(3, rows[2].Members.Count);
    }
}
