using PConTRoL.Core;
using PConTRoL.Core.Files;
using PConTRoL.Core.Logging;
using PConTRoL.Core.Parsing;
using PConTRoL.Core.Statistics;

namespace PConTRoL.Tests;

/// <summary>Kanban-Kacheln in einem temporären Hauptordner.</summary>
public sealed class KanbanTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 10, 23, 12, 0, 0, DateTimeKind.Utc);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "PConTRoL-Kanban-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly AppPaths _paths;
    private readonly StatisticsStore _statistics;

    public KanbanTests()
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

    /// <summary>Platte mit hop, lbl (inkl. einem Teil, damit die Statistik sie zählt) und jpg.</summary>
    private static string Board(string dir, string key, int minutesAgo = 300)
    {
        Directory.CreateDirectory(dir);
        var hop = Path.Combine(dir, key + ".hop");
        File.WriteAllText(hop, $";NCNAME={key}\r\nVARS\r\n   DX := 1000;\r\n   DY := 1000;\r\n   DZ := 19;\r\nSTART\r\nNC-CODE\r\n");
        File.WriteAllText(Path.Combine(dir, key + ".lbl"),
            "[Global]\r\nBoardID=1\r\n[Board]\r\nMaterial=Dekor\r\nDimX=1000\r\nDimY=1000\r\n[Part_1]\r\nDimX=500\r\nDimY=1000\r\n");
        File.WriteAllText(Path.Combine(dir, key + ".jpg"), "bild");
        foreach (var f in Directory.GetFiles(dir, key + ".*")) File.SetLastWriteTimeUtc(f, Now.AddMinutes(-minutesAgo));
        return hop;
    }

    private string KanbanDir(string folder) => Path.Combine(_paths.Kanban, folder);

    private static long IdOf(string hop) => HopProductionId.Parse(NestFileParser.Parse(hop, null).ProductionId) ?? 0;

    [Fact]
    public void Creating_a_tile_moves_hop_lbl_and_jpg_into_the_kanban_folder()
    {
        var a = Board(Path.Combine(_paths.NestResult, "JobA"), "regal_01");
        var b = Board(Path.Combine(_root, "irgendwo"), "regal_02");
        var c = Controller();

        var r = c.CreateKanbanTile("Regal 80", [a, b]);

        Assert.True(r.Success, r.Message);
        var tile = Assert.Single(c.KanbanTiles());
        Assert.Equal("Regal 80", tile.Name);
        Assert.Equal(["regal_01.hop", "regal_02.hop"], tile.Files);
        foreach (var ext in new[] { ".hop", ".lbl", ".jpg" })
        {
            Assert.True(File.Exists(Path.Combine(KanbanDir("Regal 80"), "regal_01" + ext)));
            Assert.False(File.Exists(Path.ChangeExtension(a, ext)));
        }
        Assert.Empty(c.Refresh().Scan.Stock); // nicht mehr im Vorrat
    }

    [Fact]
    public void Jobs_at_the_machine_cannot_become_kanban()
    {
        var m = Board(_paths.Input, "laeuft");
        var c = Controller();

        Assert.False(c.CreateKanbanTile("X", [m]).Success);
        Assert.True(File.Exists(m));
        Assert.Empty(c.KanbanTiles());
    }

    [Fact]
    public void Click_sends_copies_as_named_group_and_can_be_repeated_after_production()
    {
        var a = Board(Path.Combine(_root, "q"), "regal_01");
        var b = Board(Path.Combine(_root, "q"), "regal_02");
        Board(_paths.Input, "run", 100);
        var c = Controller();
        Assert.True(c.CreateKanbanTile("Regal 80", [a, b]).Success);
        var tileId = c.KanbanTiles()[0].Id;
        var originals = Directory.GetFiles(KanbanDir("Regal 80")).ToDictionary(f => f, File.ReadAllBytes);
        c.Refresh();

        Assert.True(c.SendKanbanTile(tileId).Success);

        var snap = c.Refresh();
        Assert.Equal(["run", "regal_01", "regal_02"], snap.Scan.Machine.Select(j => j.Key));
        var groupId = snap.Layout.Machine.GroupOf("regal_01");
        Assert.NotNull(groupId);
        Assert.Equal("Regal 80", snap.Layout.Machine.FindGroup(groupId!)!.Name);
        Assert.Equal(groupId, snap.Layout.Machine.GroupOf("regal_02"));
        Assert.Equal(1000, IdOf(Path.Combine(_paths.Input, "regal_01.hop")));
        Assert.Equal(1001, IdOf(Path.Combine(_paths.Input, "regal_02.hop")));
        Assert.False(File.Exists(Path.Combine(_paths.Input, "regal_01.jpg")));
        Assert.All(originals, o => Assert.Equal(o.Value, File.ReadAllBytes(o.Key))); // Originale unverändert
        Assert.False(Directory.Exists(_paths.KanbanStaging) && Directory.EnumerateFileSystemEntries(_paths.KanbanStaging).Any());

        // Noch nicht gefertigt: nicht nochmal schicken.
        Assert.False(c.SendKanbanTile(tileId).Success);

        // Maschine fertig (Dateien weg) – zweimal Refresh, damit die Statistik es sieht.
        foreach (var f in Directory.GetFiles(_paths.Input, "regal_*")) File.Delete(f);
        c.Refresh();
        Assert.True(c.SendKanbanTile(tileId).Success);
        Assert.Equal(1002, IdOf(Path.Combine(_paths.Input, "regal_01.hop")));
        c.Refresh(); // normales Einlesen sieht die Kopien an der Maschine
        foreach (var f in Directory.GetFiles(_paths.Input, "regal_*")) File.Delete(f);
        c.Refresh();

        // Jede Fertigung zählt einzeln, obwohl die .lbl gleich ist.
        Assert.Equal(4, _statistics.Load().Count);
    }

    [Fact]
    public void Locked_original_aborts_without_leaving_anything_at_the_machine()
    {
        var a = Board(Path.Combine(_root, "q"), "regal_01");
        var b = Board(Path.Combine(_root, "q"), "regal_02");
        var c = Controller();
        Assert.True(c.CreateKanbanTile("Regal", [a, b]).Success);
        var tileId = c.KanbanTiles()[0].Id;

        OperationResult r;
        using (new FileStream(Path.Combine(KanbanDir("Regal"), "regal_02.hop"), FileMode.Open, FileAccess.Read, FileShare.None))
            r = c.SendKanbanTile(tileId);

        Assert.False(r.Success);
        Assert.Empty(Directory.GetFiles(_paths.Input));
        Assert.Empty(c.Refresh().Layout.Machine.Groups);
        Assert.True(c.SendKanbanTile(tileId).Success);
    }

    [Fact]
    public void Removing_a_tile_puts_the_boards_back_into_stock_and_deletes_nothing()
    {
        var a = Board(Path.Combine(_root, "q"), "regal_01");
        var c = Controller();
        Assert.True(c.CreateKanbanTile("Regal/80", [a]).Success); // Schrägstrich ist im Ordnernamen nicht erlaubt
        var tile = c.KanbanTiles()[0];
        Assert.Equal("Regal_80", tile.Folder);

        Assert.True(c.RenameKanbanTile(tile.Id, "Regal groß").Success);
        Assert.Equal("Regal groß", c.KanbanTiles()[0].Name);

        Assert.True(c.RemoveKanbanTile(tile.Id).Success);
        Assert.Empty(c.KanbanTiles());
        Assert.False(Directory.Exists(KanbanDir("Regal_80")));
        foreach (var ext in new[] { ".hop", ".lbl", ".jpg" })
            Assert.True(File.Exists(Path.Combine(_paths.NestResult, "Regal_80", "regal_01" + ext)));
        Assert.Equal(["regal_01"], c.Refresh().Scan.Stock.Select(j => j.Key));
    }
}
