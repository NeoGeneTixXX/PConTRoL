using PConTRoL.Core;
using PConTRoL.Core.Ordering;

namespace PConTRoL.Tests;

public class TimestampPlannerTests
{
    private static readonly DateTime Now = new(2026, 10, 22, 12, 0, 0, DateTimeKind.Utc);

    private static DateTime T(int minutesAgo) => Now.AddMinutes(-minutesAgo);

    private static List<DateTime> Apply(List<(string Key, DateTime Time)> order, IReadOnlyList<TimestampChange> changes)
    {
        var map = order.ToDictionary(o => o.Key, o => o.Time);
        foreach (var c in changes) map[c.Key] = c.NewUtc;
        return order.Select(o => map[o.Key]).ToList();
    }

    private static void AssertStrictlyIncreasing(List<DateTime> times)
    {
        for (var i = 1; i < times.Count; i++)
            Assert.True(times[i] - times[i - 1] >= TimestampPlanner.MinimumStep, $"Index {i}: {times[i - 1]:O} -> {times[i]:O}");
    }

    [Fact]
    public void Already_sorted_order_changes_nothing()
    {
        var order = new List<(string, DateTime)> { ("A", T(30)), ("B", T(20)), ("C", T(10)) };
        Assert.Empty(TimestampPlanner.Plan(order, true, Now, MachineSortOrder.OldestFirst));
    }

    [Fact]
    public void Swapping_two_jobs_keeps_running_job_and_sorted_prefix()
    {
        // Gewünscht: A (läuft), C, B
        var order = new List<(string Key, DateTime Time)> { ("A", T(30)), ("C", T(10)), ("B", T(20)) };
        var changes = TimestampPlanner.Plan(order, true, Now, MachineSortOrder.OldestFirst);

        Assert.DoesNotContain(changes, c => c.Key == "A");
        Assert.DoesNotContain(changes, c => c.Key == "C"); // C ist schon jünger als A → bleibt
        var result = Apply(order, changes);
        AssertStrictlyIncreasing(result);
        Assert.All(result, t => Assert.True(t <= Now));
    }

    [Fact]
    public void Moving_job_directly_after_running_job_reorders_all_following()
    {
        var order = new List<(string Key, DateTime Time)> { ("A", T(30)), ("D", T(5)), ("B", T(20)), ("C", T(10)) };
        var changes = TimestampPlanner.Plan(order, true, Now, MachineSortOrder.OldestFirst);
        var result = Apply(order, changes);

        Assert.Equal(T(30), result[0]);
        AssertStrictlyIncreasing(result);
        Assert.All(result, t => Assert.True(t <= Now));
    }

    [Fact]
    public void Running_job_with_very_recent_date_still_wins_and_others_go_after_it()
    {
        // Läuft gerade, Datum vor 0,5 s – kaum Platz bis "jetzt".
        var running = Now.AddMilliseconds(-500);
        var order = new List<(string Key, DateTime Time)> { ("A", running), ("B", T(60)), ("C", T(50)) };
        var changes = TimestampPlanner.Plan(order, true, Now, MachineSortOrder.OldestFirst);
        var result = Apply(order, changes);

        Assert.Equal(running, result[0]);
        AssertStrictlyIncreasing(result);
    }

    [Fact]
    public void Without_running_job_new_times_end_near_now()
    {
        var order = new List<(string Key, DateTime Time)> { ("B", T(10)), ("A", T(20)) };
        var changes = TimestampPlanner.Plan(order, false, Now, MachineSortOrder.OldestFirst);
        var result = Apply(order, changes);

        AssertStrictlyIncreasing(result);
        Assert.True(result[^1] <= Now);
    }

    [Fact]
    public void Newest_first_machine_gets_descending_dates_and_keeps_running_job()
    {
        var order = new List<(string Key, DateTime Time)> { ("A", T(1)), ("B", T(30)), ("C", T(10)) };
        var changes = TimestampPlanner.Plan(order, true, Now, MachineSortOrder.NewestFirst);
        var result = Apply(order, changes);

        Assert.Equal(T(1), result[0]);
        for (var i = 1; i < result.Count; i++) Assert.True(result[i] < result[i - 1]);
    }

    [Fact]
    public void Many_jobs_get_distinct_whole_second_times()
    {
        var order = Enumerable.Range(0, 50).Select(i => ($"J{i}", T(1000 - i * 7 % 13))).ToList();
        var changes = TimestampPlanner.Plan(order, true, Now, MachineSortOrder.OldestFirst);
        var result = Apply(order, changes);

        AssertStrictlyIncreasing(result);
        Assert.All(changes, c => Assert.Equal(0, c.NewUtc.Ticks % TimeSpan.TicksPerSecond));
    }
}
