using PConTRoL.Core;
using PConTRoL.Core.Files;
using PConTRoL.Core.Layout;
using PConTRoL.Core.Logging;
using PConTRoL.Core.Statistics;

namespace PConTRoL.Tests;

/// <summary>Löschen = in den Papierkorb im Hauptordner verschieben.</summary>
public sealed class DeleteTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 10, 23, 12, 0, 0, DateTimeKind.Utc);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "PConTRoL-Delete-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly AppPaths _paths;
    private readonly StatisticsStore _statistics;

    public DeleteTests()
    {
        _paths = new AppPaths(_root);
        Directory.CreateDirectory(_paths.Input);
        Directory.CreateDirectory(_paths.NestResult);
        _statistics = new StatisticsStore(_paths.StatisticsFile);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private QueueController Controller() =>
        new(_paths, new ActionLog(_paths.Shared, Path.Combine(_root, "fallback")), new RetryPolicy(2, TimeSpan.FromMilliseconds(10)), () => Now,
            statistics: _statistics);

    private static void Board(string dir, string key, int minutesAgo, params string[] extras)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, key + ".hop"), $";NCNAME={key}\r\nVARS\r\n   DX := 1000;\r\n   DY := 1000;\r\nSTART\r\n");
        File.WriteAllText(Path.Combine(dir, key + ".lbl"),
            "[Global]\r\nBoardID=1\r\n[Board]\r\nMaterial=Dekor\r\nDimX=1000\r\nDimY=1000\r\n[Part_1]\r\nDimX=500\r\nDimY=1000\r\n");
        foreach (var ext in extras) File.WriteAllText(Path.Combine(dir, key + ext), "x");
        foreach (var f in Directory.GetFiles(dir, key + ".*")) File.SetLastWriteTimeUtc(f, Now.AddMinutes(-minutesAgo));
    }

    private string Bin => Path.Combine(_paths.RecycleBin, Now.ToLocalTime().ToString("yyyy-MM-dd_HHmmss"));

    [Fact]
    public void Deleted_stock_job_goes_to_the_recycle_bin_with_all_its_files()
    {
        var job = Path.Combine(_paths.NestResult, "JobA");
        Board(job, "a1", 300, ".jpg", ".ups");
        Board(job, "a1.b", 300); // ähnlicher Name, darf nicht mit
        var c = Controller();
        c.Refresh();

        var r = c.Delete(Side.Stock, Selection.OfKeys("a1"));

        Assert.True(r.Success, r.Message);
        foreach (var ext in new[] { ".hop", ".lbl", ".jpg", ".ups" })
        {
            Assert.False(File.Exists(Path.Combine(job, "a1" + ext)));
            Assert.True(File.Exists(Path.Combine(Bin, "_NestResult", "JobA", "a1" + ext)));
        }
        Assert.True(File.Exists(Path.Combine(job, "a1.b.hop")));
        Assert.Equal(["a1.b"], c.Refresh().Layout.Stock.Order);
    }

    [Fact]
    public void Empty_job_folder_is_cleaned_up_and_whole_group_can_be_deleted()
    {
        var job = Path.Combine(_paths.NestResult, "JobA");
        Board(job, "a1", 300);
        Board(job, "a2", 300);
        var c = Controller();
        c.Refresh();

        Assert.True(c.Delete(Side.Stock, Selection.OfGroup("auto:JobA")).Success);

        Assert.False(Directory.Exists(job));
        Assert.True(Directory.Exists(_paths.NestResult));
        var snap = c.Refresh();
        Assert.Empty(snap.Scan.Stock);
        Assert.Empty(snap.Layout.Stock.Groups);
    }

    [Fact]
    public void Machine_job_is_deleted_but_never_the_running_one_and_never_counted_as_finished()
    {
        Board(_paths.Input, "run", 100);
        Board(_paths.Input, "m2", 90);
        var c = Controller();
        c.Refresh();

        var r = c.Delete(Side.Machine, Selection.OfKeys("run", "m2"));

        Assert.True(r.Success, r.Message);
        Assert.Contains("run", r.Message); // Hinweis: läuft, nicht gelöscht
        Assert.True(File.Exists(Path.Combine(_paths.Input, "run.hop")));
        Assert.True(File.Exists(Path.Combine(Bin, "_Input", "m2.hop")));
        c.Refresh();
        c.Refresh();
        Assert.Empty(_statistics.Load());
        Assert.Equal(["run"], c.Refresh().Scan.Machine.Select(j => j.Key));

        Assert.False(c.Delete(Side.Machine, Selection.OfKeys("run")).Success);
    }

    [Fact]
    public void Locked_file_rolls_back_the_whole_delete()
    {
        var job = Path.Combine(_paths.NestResult, "JobA");
        Board(job, "a1", 300);
        Board(job, "a2", 300);
        var c = Controller();
        c.Refresh();

        OperationResult r;
        using (new FileStream(Path.Combine(job, "a2.lbl"), FileMode.Open, FileAccess.Read, FileShare.Read))
            r = c.Delete(Side.Stock, Selection.OfKeys("a1", "a2"));

        Assert.False(r.Success);
        Assert.True(r.CanRetry);
        Assert.Equal(4, Directory.GetFiles(job).Length);
        Assert.Equal(2, c.Refresh().Scan.Stock.Count);
    }
}
