using System.Text;
using PConTRoL.Core;
using PConTRoL.Core.Files;
using PConTRoL.Core.Layout;
using PConTRoL.Core.Logging;
using PConTRoL.Core.Parsing;
using PConTRoL.Core.Statistics;

namespace PConTRoL.Tests;

public sealed class StatisticsTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 10, 22, 12, 0, 0, DateTimeKind.Utc);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "PConTRoL-Stat-" + Guid.NewGuid().ToString("N")[..8]);

    public StatisticsTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private static string Data(string name) => Path.Combine(AppContext.BaseDirectory, "TestData", name);

    /// <summary>Schreibt hop + lbl (UTF-16 wie BetterNest) mit Platten- und Teilemaßen.</summary>
    private static void WriteJob(string dir, string key, string material, double boardX, double boardY,
        params (double W, double H)[] parts)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, key + ".hop"),
            $";NCNAME={key}\r\nVARS\r\n   DX := {boardX};\r\n   DY := {boardY};\r\n   DZ := 12;\r\nSTART\r\n");
        var sb = new StringBuilder($"[Global]\r\nHopFilename=X:\\Beispiel\\_NestResult\\Job\\{key}.hop\r\nBoardID=1\r\n")
            .Append($"[Board]\r\nMaterial={material}\r\nDimX={boardX}\r\nDimY={boardY}\r\nPartsOnBoard={parts.Length}\r\n");
        for (var i = 0; i < parts.Length; i++)
            sb.Append($"[Part_{i + 1}]\r\nDimX={parts[i].W}\r\nDimY={parts[i].H}\r\nCount={i + 7}\r\nFreeText4=RD{i}\r\n");
        File.WriteAllText(Path.Combine(dir, key + ".lbl"), sb.ToString(), Encoding.Unicode);
    }

    private static BoardRecord Record(string id, string material, double board, double used, int day = 1) =>
        BoardRecord.Create(id, new DateTime(2026, 10, day, 8, 0, 0, DateTimeKind.Utc), material, board, 1, used, "Job", id, BoardRecord.SourceMachine)!;

    // ---------- Daten aus der .lbl ----------

    [Fact]
    public void Used_area_is_sum_of_part_rectangles_and_count_is_ignored()
    {
        const string name = "MUSTER_KW01_1_Montag(XX)_Beispielplatte_12mm_01";
        var info = NestFileParser.Parse(Data(name + ".hop"), Data(name + ".lbl"));

        Assert.Equal(2000 * 2400 + 2000 * 30 + 2000 * 2800, info.UsedArea);
        Assert.Equal(64, info.LblHash!.Length);

        WriteJob(_root, "x", "M", 1000, 1000, (500, 500), (200, 100));
        var own = NestFileParser.Parse(Path.Combine(_root, "x.hop"), Path.Combine(_root, "x.lbl"));
        Assert.Equal(270_000, own.UsedArea); // Count=7/8 in der Datei darf nicht multipliziert werden
    }

    [Fact]
    public void Record_computes_utilization_and_waste_and_rejects_unusable_boards()
    {
        var r = BoardRecord.Create("id", Now, "  Dekor   12mm ", 2000, 1000, 1_500_000, "Job", "f", BoardRecord.SourceMachine)!;
        Assert.Equal(2_000_000, r.BoardArea);
        Assert.Equal(75, r.Utilization);
        Assert.Equal(25, r.Waste);
        Assert.Equal("Dekor 12mm", r.Material);

        Assert.Null(BoardRecord.Create(null, Now, "M", 1, 1, 1, "", "", ""));               // keine .lbl
        Assert.Null(BoardRecord.Create("id", Now, "M", 1000, null, 1, "", "", ""));        // Maß fehlt
        Assert.Null(BoardRecord.Create("id", Now, "M", 1000, 1000, null, "", "", ""));     // keine Teile
        Assert.Null(BoardRecord.Create("id", Now, "M", 1000, 1000, 2_000_000, "", "", "")); // mehr Teile als Platte
        Assert.Equal(BoardRecord.NoMaterial, BoardRecord.Create("id", Now, " ", 10, 10, 50, "", "", "")!.Material);
    }

    [Fact]
    public void Material_names_are_normalized_for_grouping()
    {
        Assert.Equal("Dekor 12mm", MaterialName.Normalize("  Dekor \t 12mm "));
        Assert.Equal(MaterialName.Key("Dekor 12mm"), MaterialName.Key(" dekor  12MM"));
        Assert.NotEqual(MaterialName.Key("Dekor 12mm"), MaterialName.Key("Dekor 19mm"));
    }

    // ---------- Auswertung ----------

    [Fact]
    public void Average_per_material_is_area_weighted_and_materials_are_dynamic()
    {
        var records = new[]
        {
            Record("a", "Dekor 12mm", 1_000_000, 900_000, day: 1),   // 90 %
            Record("b", " dekor  12MM", 4_000_000, 2_000_000, day: 2), // 50 %, gleiche Sorte
            Record("c", "Neues Material", 1_000_000, 800_000, day: 3),
        };

        var stats = StatisticsService.ByMaterial(records);

        Assert.Equal(["Dekor 12mm", "Neues Material"], stats.Select(s => s.Material));
        var dekor = stats[0];
        Assert.Equal(2, dekor.Boards);
        Assert.Equal(58, dekor.Utilization, 6); // 2,9 / 5 m² – nicht (90 + 50) / 2 = 70
        Assert.Equal(42, dekor.Waste, 6);
        Assert.Equal(80, stats[1].Utilization, 6);

        var total = StatisticsService.Total(records);
        Assert.Equal(3, total.Boards);
        Assert.Equal(3_700_000.0 / 6_000_000 * 100, total.Utilization, 6);
    }

    [Fact]
    public void Time_filter_is_inclusive_start_exclusive_end()
    {
        var records = new[] { Record("a", "M", 10, 5, day: 1), Record("b", "M", 10, 5, day: 2), Record("c", "M", 10, 5, day: 3) };
        var from = new DateTime(2026, 10, 2, 0, 0, 0, DateTimeKind.Utc);
        var to = new DateTime(2026, 10, 3, 0, 0, 0, DateTimeKind.Utc);

        Assert.Equal(["b"], StatisticsService.Filter(records, from, to).Select(r => r.Id));
        Assert.Equal(["b", "c"], StatisticsService.Filter(records, from, null).Select(r => r.Id));
    }

    // ---------- Speicherung ----------

    [Fact]
    public void Store_persists_prevents_duplicates_and_survives_broken_lines()
    {
        var path = Path.Combine(_root, "_PConTRoL", "statistik.jsonl");
        var store = new StatisticsStore(path);

        Assert.True(store.Add(Record("a", "M", 100, 50)));
        Assert.False(store.Add(Record("A", "M", 100, 50))); // gleiche Id (Groß/klein egal)
        Assert.Equal(1, store.AddRange([Record("b", "M", 100, 50), Record("a", "M", 100, 50)]));

        // Anderer Rechner hat dieselbe Platte parallel angehängt + abgebrochene Zeile
        File.AppendAllText(path, File.ReadLines(path).First() + "\n{\"Id\":\"kaputt\n");

        var reloaded = new StatisticsStore(path).Load();
        Assert.Equal(["a", "b"], reloaded.Select(r => r.Id));
        Assert.True(new StatisticsStore(path).Contains("b"));
    }

    [Fact]
    public void Archive_import_reads_subfolders_and_skips_known_and_invalid_boards()
    {
        var archive = Path.Combine(_root, "Archiv");
        WriteJob(Path.Combine(archive, "2025", "JobA"), "a_01", "Dekor 12mm", 1000, 1000, (500, 1000));
        WriteJob(Path.Combine(archive, "2026"), "b_01", "Spanplatte 19mm", 2000, 1000, (1000, 1000), (500, 1000));
        File.WriteAllText(Path.Combine(archive, "kaputt.lbl"), "[Global]\r\nBoardID=1\r\n");
        // gleiche Platte noch einmal als Kopie
        Directory.CreateDirectory(Path.Combine(archive, "Kopie"));
        File.Copy(Path.Combine(archive, "2025", "JobA", "a_01.lbl"), Path.Combine(archive, "Kopie", "a_01.lbl"));

        var store = new StatisticsStore(Path.Combine(_root, "statistik.jsonl"));
        var first = ArchiveImporter.Import(archive, store);

        Assert.Equal(new ArchiveImportResult(Files: 4, Added: 2, Known: 1, Invalid: 1), first);
        var records = store.Load();
        Assert.All(records, r => Assert.Equal(BoardRecord.SourceArchive, r.Source));
        Assert.Equal(75, records.Single(r => r.Material == "Spanplatte 19mm").Utilization);

        var again = ArchiveImporter.Import(archive, store);
        Assert.Equal(0, again.Added);
        Assert.Equal(3, again.Known);
    }

    [Fact]
    public void Csv_uses_german_number_format()
    {
        var records = new[] { Record("a", "Dekor; 12mm", 1_000_000, 750_000) };
        using var w = new StringWriter();
        StatisticsCsv.WriteSummary(w, StatisticsService.ByMaterial(records), StatisticsService.Total(records));

        var lines = w.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.StartsWith("Material;Platten;Ausnutzung %", lines[0]);
        Assert.Equal("\"Dekor; 12mm\";1;75,00;25,00;1,000;0,750;0,250", lines[1]);
        Assert.StartsWith("Alle Materialien;1;75,00", lines[2]);
    }

    // ---------- Erkennung "Maschine fertig" ----------

    [Fact]
    public void Board_finished_by_machine_is_recorded_once_but_returned_board_is_not()
    {
        var paths = new AppPaths(Path.Combine(_root, "Haupt"));
        Directory.CreateDirectory(paths.NestResult);
        WriteJob(paths.Input, "run", "Dekor", 1000, 1000, (800, 1000));
        WriteJob(paths.Input, "next", "Dekor", 1000, 1000, (500, 1000));
        File.SetLastWriteTimeUtc(Path.Combine(paths.Input, "run.hop"), Now.AddMinutes(-10));
        var store = new StatisticsStore(paths.StatisticsFile);
        var c = new QueueController(paths, new ActionLog(paths.Shared, Path.Combine(_root, "fb")),
            new RetryPolicy(2, TimeSpan.FromMilliseconds(10)), () => Now, statistics: store);

        c.Refresh();
        Assert.Empty(store.Load()); // erster Durchlauf: nichts ist "fertig"

        // PConTRoL holt "next" zurück in den Vorrat → keine Statistik
        Assert.True(c.Move(Side.Machine, Side.Stock, Selection.OfKeys("next"), DropTarget.End).Success);
        c.Refresh();
        Assert.Empty(store.Load());

        // Maschine ist mit "run" fertig: kopiert ins Archiv und löscht aus _Input
        File.Delete(Path.Combine(paths.Input, "run.hop"));
        File.Delete(Path.Combine(paths.Input, "run.lbl"));
        c.Refresh();
        c.Refresh();

        var record = Assert.Single(store.Load());
        Assert.Equal("run", record.File);
        Assert.Equal(80, record.Utilization);
        Assert.Equal(BoardRecord.SourceMachine, record.Source);
        Assert.Equal(Now, record.TimeUtc);
    }
}
