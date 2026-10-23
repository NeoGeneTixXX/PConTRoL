using PConTRoL.Core;
using PConTRoL.Core.Automation;
using PConTRoL.Core.Files;
using PConTRoL.Core.Layout;
using PConTRoL.Core.Logging;
using PConTRoL.Core.Parsing;

namespace PConTRoL.Tests;

/// <summary>Testet echtes Verschieben und Datum-Setzen in einem temporären Ordner.</summary>
public sealed class QueueControllerTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 10, 22, 12, 0, 0, DateTimeKind.Utc);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "PConTRoL-Test-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly AppPaths _paths;

    public QueueControllerTests()
    {
        _paths = new AppPaths(_root);
        Directory.CreateDirectory(_paths.Input);
        Directory.CreateDirectory(_paths.NestResult);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private QueueController Controller() =>
        new(_paths, new ActionLog(_paths.Shared, Path.Combine(_root, "fallback")), new RetryPolicy(2, TimeSpan.FromMilliseconds(10)), () => Now);

    private void StockJob(string nestJob, string key, int minutesAgo, int board = 1, bool jpg = true)
    {
        var dir = Path.Combine(_paths.NestResult, nestJob);
        Directory.CreateDirectory(dir);
        WriteJob(dir, key, nestJob, minutesAgo, board);
        if (jpg) File.WriteAllText(Path.Combine(dir, key + ".jpg"), "bild");
        File.WriteAllText(Path.Combine(dir, key + ".ups"), "ups");
    }

    private void MachineJob(string nestJob, string key, int minutesAgo, long? productionId = null) =>
        WriteJob(_paths.Input, key, nestJob, minutesAgo, 1, productionId);

    private static void WriteJob(string dir, string key, string nestJob, int minutesAgo, int board, long? productionId = null)
    {
        var hop = Path.Combine(dir, key + ".hop");
        var lbl = Path.Combine(dir, key + ".lbl");
        var id = productionId is { } p ? $"_hhdata_ProductionID := {p};\r\n" : "";
        File.WriteAllText(hop, $";NCNAME={key}\r\nVARS\r\n   DX := 5600;\r\n   DY := 2100;\r\n   DZ := 12;\r\n   _hhdata_Material := 'Dekor';\r\n_hhdata_ProdPrio := 50;\r\n{id}START\r\nNC-CODE\r\n");
        File.WriteAllText(lbl, $"[Global]\r\nHopFilename=X:\\Beispiel\\_NestResult\\{nestJob}\\{key}.hop\r\nBoardCount=9\r\nBoardID={board}\r\n[Board]\r\nMaterial=Dekor\r\n");
        var t = Now.AddMinutes(-minutesAgo);
        File.SetLastWriteTimeUtc(hop, t);
        File.SetLastWriteTimeUtc(lbl, t);
    }

    /// <summary>Reihenfolge so, wie der JobManager sortiert: Production ID, dann Datum.</summary>
    private List<string> MachineOrderOnDisk() =>
        Directory.GetFiles(_paths.Input, "*.hop")
            .Select(f => (Key: Path.GetFileNameWithoutExtension(f), Id: ProductionIdOf(f), Time: File.GetLastWriteTimeUtc(f)))
            .OrderBy(x => x.Id).ThenBy(x => x.Time).ThenBy(x => x.Key)
            .Select(x => x.Key).ToList();

    private static long ProductionIdOf(string hop) =>
        HopProductionId.Parse(NestFileParser.Parse(hop, null).ProductionId) ?? 0;

    [Fact]
    public void Send_to_end_moves_hop_and_lbl_but_leaves_preview_and_ups()
    {
        MachineJob("J0", "run", 100);
        MachineJob("J0", "m2", 90);
        StockJob("JobA", "a1", 300);
        var c = Controller();
        c.Refresh();

        var r = c.Move(Side.Stock, Side.Machine, Selection.OfKeys("a1"), DropTarget.End);

        Assert.True(r.Success, r.Message);
        Assert.True(File.Exists(Path.Combine(_paths.Input, "a1.hop")));
        Assert.True(File.Exists(Path.Combine(_paths.Input, "a1.lbl")));
        Assert.False(File.Exists(Path.Combine(_paths.Input, "a1.jpg")));
        Assert.True(File.Exists(Path.Combine(_paths.NestResult, "JobA", "a1.jpg")));
        Assert.True(File.Exists(Path.Combine(_paths.NestResult, "JobA", "a1.ups")));
        Assert.Equal(["run", "m2", "a1"], MachineOrderOnDisk());
        Assert.Equal(Now.AddMinutes(-100), File.GetLastWriteTimeUtc(Path.Combine(_paths.Input, "run.hop")));
        Assert.Equal(File.GetLastWriteTimeUtc(Path.Combine(_paths.Input, "a1.hop")), File.GetLastWriteTimeUtc(Path.Combine(_paths.Input, "a1.lbl")));
    }

    [Fact]
    public void Sent_jobs_always_queue_at_the_end_in_sending_order()
    {
        MachineJob("J0", "run", 100);
        MachineJob("J0", "m2", 90);
        StockJob("JobA", "a1", 300);
        StockJob("JobB", "b1", 400);
        var c = Controller();

        // Auch ein Ziel "ganz oben" bzw. ein Drop zwischen zwei Aufträgen landet am Ende.
        Assert.True(c.Move(Side.Stock, Side.Machine, Selection.OfKeys("a1"), DropTarget.Start).Success);
        Assert.True(c.Move(Side.Stock, Side.Machine, Selection.OfKeys("b1"), new DropTarget(DropKind.BeforeJob, "m2")).Success);

        Assert.Equal(["run", "m2", "a1", "b1"], MachineOrderOnDisk());
        Assert.Equal(["run", "m2", "a1", "b1"], c.Refresh().Scan.Machine.Select(j => j.Key));
        Assert.Equal(Now.AddMinutes(-100), File.GetLastWriteTimeUtc(Path.Combine(_paths.Input, "run.hop")));
    }

    [Fact]
    public void Machine_order_cannot_be_changed()
    {
        MachineJob("J0", "run", 100);
        MachineJob("J0", "m2", 90);
        MachineJob("J0", "m3", 80);
        MachineJob("J0", "m4", 70);
        var c = Controller();
        var dates = Directory.GetFiles(_paths.Input).ToDictionary(f => f, File.GetLastWriteTimeUtc);

        var step = c.Step(Side.Machine, Selection.OfKeys("m4"), MoveStep.Top);
        var move = c.Move(Side.Machine, Side.Machine, Selection.OfKeys("m2"), DropTarget.End);

        Assert.False(step.Success);
        Assert.False(move.Success);
        Assert.Equal(QueueController.MachineOrderFixedMessage, step.Message);
        Assert.Equal(["run", "m2", "m3", "m4"], MachineOrderOnDisk());
        Assert.All(dates, d => Assert.Equal(d.Value, File.GetLastWriteTimeUtc(d.Key)));
    }

    [Fact]
    public void Group_can_be_sorted_in_stock_but_not_at_the_machine()
    {
        MachineJob("J0", "run", 100);
        StockJob("JobA", "a_10", 300, board: 1);
        StockJob("JobA", "a_2", 300, board: 2);
        StockJob("JobB", "b_10", 200, board: 1);
        StockJob("JobB", "b_2", 200, board: 2);
        var c = Controller();
        c.Refresh();

        Assert.True(c.SortGroupByName(Side.Stock, "auto:JobA").Changed);
        Assert.Equal(["a_2", "a_10"], c.Refresh().Layout.Stock.MembersOf("auto:JobA"));
        Assert.True(c.Step(Side.Stock, Selection.OfKeys("a_10"), MoveStep.Top).Changed);
        Assert.Equal(["a_10", "a_2"], c.Refresh().Layout.Stock.MembersOf("auto:JobA"));

        Assert.True(c.Move(Side.Stock, Side.Machine, Selection.OfGroup("auto:JobB"), DropTarget.End).Success);
        Assert.False(c.SortGroupByName(Side.Machine, "auto:JobB").Success);
        Assert.False(c.Step(Side.Machine, Selection.OfKeys("b_2"), MoveStep.Top).Success);
        Assert.Equal(["run", "b_10", "b_2"], MachineOrderOnDisk());
    }

    [Fact]
    public void Locked_file_aborts_and_rolls_back_everything()
    {
        MachineJob("J0", "run", 100);
        MachineJob("J0", "m2", 90);
        StockJob("JobA", "a1", 300);
        StockJob("JobA", "a2", 300, board: 2);
        var c = Controller();
        var a1Lbl = Path.Combine(_paths.NestResult, "JobA", "a1.lbl");
        var oldTime = File.GetLastWriteTimeUtc(a1Lbl);
        var a1Hop = File.ReadAllBytes(Path.Combine(_paths.NestResult, "JobA", "a1.hop"));

        OperationResult r;
        using (new FileStream(Path.Combine(_paths.NestResult, "JobA", "a2.hop"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            r = c.Move(Side.Stock, Side.Machine, Selection.OfKeys("a1", "a2"), DropTarget.End);
        }

        Assert.False(r.Success);
        Assert.True(r.CanRetry);
        Assert.Equal(["run", "m2"], MachineOrderOnDisk());
        Assert.True(File.Exists(a1Lbl));
        Assert.True(File.Exists(Path.Combine(_paths.NestResult, "JobA", "a1.hop")));
        Assert.False(File.Exists(Path.Combine(_paths.Input, "a1.lbl")));
        Assert.Equal(oldTime, File.GetLastWriteTimeUtc(a1Lbl));
        Assert.Equal(a1Hop, File.ReadAllBytes(Path.Combine(_paths.NestResult, "JobA", "a1.hop"))); // Production ID zurückgenommen

        // Danach (Sperre weg) klappt es.
        Assert.True(c.Move(Side.Stock, Side.Machine, Selection.OfKeys("a1", "a2"), DropTarget.End).Success);
        Assert.Equal(["run", "m2", "a1", "a2"], MachineOrderOnDisk());
    }

    [Fact]
    public void Sent_jobs_get_rising_production_ids_above_everything_at_the_machine()
    {
        MachineJob("J0", "run", 100, productionId: 234540);
        StockJob("JobA", "a1", 300, board: 1);
        StockJob("JobA", "a2", 300, board: 2);
        StockJob("JobB", "b1", 200);
        var c = Controller();

        Assert.True(c.Move(Side.Stock, Side.Machine, Selection.OfGroup("auto:JobA"), DropTarget.End).Success);
        Assert.Equal(234541, ProductionIdOf(Path.Combine(_paths.Input, "a1.hop")));
        Assert.Equal(234542, ProductionIdOf(Path.Combine(_paths.Input, "a2.hop")));

        // "run" ist fertig und verschwindet – die Zählung läuft trotzdem weiter, auch auf anderen Rechnern.
        File.Delete(Path.Combine(_paths.Input, "run.hop"));
        File.Delete(Path.Combine(_paths.Input, "run.lbl"));
        Assert.True(Controller().Move(Side.Stock, Side.Machine, Selection.OfKeys("b1"), DropTarget.End).Success);
        Assert.Equal(234543, ProductionIdOf(Path.Combine(_paths.Input, "b1.hop")));
        Assert.Equal(["a1", "a2", "b1"], MachineOrderOnDisk());
        Assert.Equal(["a1", "a2", "b1"], c.Refresh().Scan.Machine.Select(j => j.Key));
    }

    [Fact]
    public void Machine_order_follows_production_id_before_date_and_files_there_are_never_touched()
    {
        MachineJob("J0", "ohne", 10);                         // keine ID = 0 -> zuerst
        MachineJob("J0", "spaet", 95, productionId: 1500);    // ID zählt vor Datum
        MachineJob("J0", "frueh", 20, productionId: 1200);
        StockJob("JobA", "a1", 300);
        var c = Controller();
        Assert.Equal(["ohne", "frueh", "spaet"], c.Refresh().Scan.Machine.Select(j => j.Key));
        var before = Directory.GetFiles(_paths.Input).ToDictionary(f => f, f => (File.GetLastWriteTimeUtc(f), File.ReadAllBytes(f)));

        Assert.True(c.Move(Side.Stock, Side.Machine, Selection.OfKeys("a1"), DropTarget.End).Success);

        Assert.Equal(1501, ProductionIdOf(Path.Combine(_paths.Input, "a1.hop")));
        Assert.Equal(["ohne", "frueh", "spaet", "a1"], c.Refresh().Scan.Machine.Select(j => j.Key));
        Assert.All(before, b =>
        {
            Assert.Equal(b.Value.Item1, File.GetLastWriteTimeUtc(b.Key));
            Assert.Equal(b.Value.Item2, File.ReadAllBytes(b.Key));
        });
    }

    [Fact]
    public void Return_to_stock_goes_back_into_job_folder_with_preview()
    {
        MachineJob("J0", "run", 100);
        MachineJob("JobB", "b1", 90);
        File.WriteAllText(Path.Combine(_paths.Input, "b1.jpg"), "bild");
        var c = Controller();

        var r = c.Move(Side.Machine, Side.Stock, Selection.OfKeys("b1"), DropTarget.End);

        Assert.True(r.Success, r.Message);
        Assert.True(File.Exists(Path.Combine(_paths.NestResult, "JobB", "b1.hop")));
        Assert.True(File.Exists(Path.Combine(_paths.NestResult, "JobB", "b1.lbl")));
        Assert.True(File.Exists(Path.Combine(_paths.NestResult, "JobB", "b1.jpg")));
        Assert.Equal(["run"], MachineOrderOnDisk());
        Assert.Contains("b1", c.Refresh().Layout.Stock.Order);
    }

    [Fact]
    public void Returned_job_goes_back_into_its_job_group()
    {
        MachineJob("J0", "run", 100);
        StockJob("JobA", "a1", 300, board: 1);
        StockJob("JobA", "a2", 300, board: 2);
        var c = Controller();
        c.Refresh();
        c.Move(Side.Stock, Side.Machine, Selection.OfKeys("a1"), DropTarget.End);

        Assert.True(c.Move(Side.Machine, Side.Stock, Selection.OfKeys("a1"), DropTarget.End).Success);

        var stock = c.Refresh().Layout.Stock;
        Assert.Equal("auto:JobA", stock.GroupOf("a1"));
        Assert.Equal(["a2", "a1"], stock.Order);
    }

    [Fact]
    public void Running_job_cannot_be_returned()
    {
        MachineJob("J0", "run", 100);
        var c = Controller();
        var r = c.Move(Side.Machine, Side.Stock, Selection.OfKeys("run"), DropTarget.End);
        Assert.False(r.Success);
        Assert.True(File.Exists(Path.Combine(_paths.Input, "run.hop")));
    }

    [Fact]
    public void Existing_destination_is_never_overwritten()
    {
        MachineJob("J0", "run", 100);
        MachineJob("JobB", "b1", 90);
        Directory.CreateDirectory(Path.Combine(_paths.NestResult, "JobB"));
        File.WriteAllText(Path.Combine(_paths.NestResult, "JobB", "b1.hop"), "ANDERE DATEI");
        var c = Controller();

        var r = c.Move(Side.Machine, Side.Stock, Selection.OfKeys("b1"), DropTarget.End);

        Assert.False(r.Success);
        Assert.Equal("ANDERE DATEI", File.ReadAllText(Path.Combine(_paths.NestResult, "JobB", "b1.hop")));
        Assert.True(File.Exists(Path.Combine(_paths.Input, "b1.hop")));
    }

    [Fact]
    public void Stock_order_and_groups_are_persisted_for_other_computers()
    {
        StockJob("JobA", "a1", 300, board: 1);
        StockJob("JobA", "a2", 300, board: 2);
        StockJob("JobB", "b1", 200);
        var c = Controller();
        var snap = c.Refresh();
        Assert.Equal("auto:JobA", snap.Layout.Stock.GroupOf("a1"));

        c.Move(Side.Stock, Side.Stock, Selection.OfKeys("b1"), DropTarget.Start);

        var other = Controller(); // "anderer Rechner"
        var s2 = other.Refresh();
        Assert.Equal(["b1", "a1", "a2"], s2.Layout.Stock.Order);
        Assert.Equal("auto:JobA", s2.Layout.Stock.GroupOf("a2"));
    }

    [Fact]
    public void Whole_group_sent_to_machine_stays_a_group_there()
    {
        MachineJob("J0", "run", 100);
        StockJob("JobA", "a1", 300, board: 1);
        StockJob("JobA", "a2", 300, board: 2);
        var c = Controller();
        c.Refresh();

        Assert.True(c.Move(Side.Stock, Side.Machine, Selection.OfGroup("auto:JobA"), DropTarget.End).Success);

        var snap = c.Refresh();
        Assert.Equal(["run", "a1", "a2"], snap.Scan.Machine.Select(j => j.Key));
        Assert.Equal("auto:JobA", snap.Layout.Machine.GroupOf("a1"));
        Assert.Empty(snap.Layout.Stock.Groups);
    }

    [Fact]
    public void Auto_feed_sends_top_group_complete_only_when_enabled_and_machine_empty()
    {
        StockJob("JobA", "a1", 300, board: 1);
        StockJob("JobA", "a2", 300, board: 2);
        StockJob("JobB", "b1", 200);
        MachineJob("J0", "run", 100);
        var c = Controller();

        Assert.False(c.FeedNext().Changed); // Automatik aus

        Assert.True(c.SetAutoMode(true).Changed);
        Assert.True(Controller().Refresh().Layout.AutoMode); // gilt auch für andere Rechner
        Assert.False(c.FeedNext().Changed); // Maschine nicht leer

        File.Delete(Path.Combine(_paths.Input, "run.hop"));
        File.Delete(Path.Combine(_paths.Input, "run.lbl"));
        var r = c.FeedNext();

        Assert.True(r.Success && r.Changed, r.Message);
        Assert.Equal(["a1", "a2"], MachineOrderOnDisk());
        Assert.False(c.FeedNext().Changed); // Maschine wieder belegt
        Assert.Equal(["b1"], c.Refresh().Layout.Stock.Order);
    }

    [Fact]
    public void Auto_feed_skips_while_another_computer_holds_the_lock()
    {
        StockJob("JobA", "a1", 300);
        var c = Controller();
        c.SetAutoMode(true);

        using (new FileStream(_paths.AutoLockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            Assert.False(c.FeedNext().Changed);
        Assert.Empty(MachineOrderOnDisk());

        Assert.True(c.FeedNext().Changed);
        Assert.Equal(["a1"], MachineOrderOnDisk());
    }

    [Fact]
    public void Auto_feeder_waits_sixty_seconds_of_empty_machine()
    {
        var f = new AutoFeeder();
        Assert.Equal(AutoFeedState.Off, f.Observe(false, 0, 3, Now).State);
        Assert.Equal(AutoFeedState.MachineBusy, f.Observe(true, 1, 3, Now).State);

        var waiting = f.Observe(true, 0, 3, Now);
        Assert.Equal(new AutoFeedStatus(AutoFeedState.Waiting, TimeSpan.FromSeconds(60)), waiting);
        Assert.Equal(AutoFeedState.Waiting, f.Observe(true, 0, 3, Now.AddSeconds(59)).State);
        Assert.Equal(AutoFeedState.Due, f.Observe(true, 0, 3, Now.AddSeconds(60)).State);

        // Zwischendurch wieder belegt: Zählung beginnt von vorn.
        f.Observe(true, 1, 3, Now.AddSeconds(61));
        Assert.Equal(AutoFeedState.Waiting, f.Observe(true, 0, 3, Now.AddSeconds(62)).State);
        Assert.Equal(AutoFeedState.NoStock, f.Observe(true, 0, 0, Now.AddSeconds(200)).State);
        Assert.Equal(AutoFeedState.Due, f.Observe(true, 0, 1, Now.AddSeconds(201)).State);
    }

    [Fact]
    public void Every_file_action_is_logged_with_old_and_new_date()
    {
        MachineJob("J0", "run", 100);
        StockJob("JobA", "a1", 300);
        var c = Controller();
        c.Move(Side.Stock, Side.Machine, Selection.OfKeys("a1"), DropTarget.End);

        var log = File.ReadAllLines(Directory.GetFiles(_paths.Shared, "aktionen_*.log").Single());
        Assert.Contains(log, l => l.Contains("| DATUM |") && l.Contains("a1.hop"));
        Assert.Contains(log, l => l.Contains("| VERSCHOBEN |") && l.Contains("a1.lbl"));
    }
}
