using PConTRoL.Core;
using PConTRoL.Core.Database;
using PConTRoL.Core.Scanning;
using PConTRoL.Core.Statistics;
using PConTRoL.Core.Timing;

namespace PConTRoL.Tests;

public sealed class RunTimeTests : IDisposable
{
    private static readonly DateTime T0 = new(2026, 10, 24, 6, 0, 0, DateTimeKind.Utc);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "PConTRoL-Lauf-" + Guid.NewGuid().ToString("N")[..8]);

    public RunTimeTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private static JobInfo Job(string key, string material = "Dekor Weiß 19mm", double dx = 2800, double dy = 2070, int parts = 14, long pid = 1000) =>
        new()
        {
            Key = key, Side = Side.Machine, HopPath = Path.Combine("X:", key + ".hop"), NestJob = "Job",
            Material = material, DimX = dx, DimY = dy, DimZ = 19, PartsOnBoard = parts, ProductionId = pid, ContentHash = "H" + key,
        };

    private static ScanResult Scan(params JobInfo[] machine) => new() { Stock = [], Machine = machine };

    private static RunTimeRecord Rec(string id, double minutes, string material = "Dekor Weiß 19mm", double dx = 2800, double dy = 2070,
        int parts = 14, int day = 1) =>
        new()
        {
            Id = id, Material = material, DimX = dx, DimY = dy, DimZ = 19, Parts = parts, Seconds = minutes * 60,
            StartUtc = T0.AddDays(day), EndUtc = T0.AddDays(day).AddMinutes(minutes),
        };

    // ---------- Messen ----------

    [Fact]
    public void Tracker_measures_from_moving_to_top_until_finished_and_skips_job_running_at_start()
    {
        var a = Job("a", pid: 1);
        var b = Job("b", pid: 2);
        var c = Job("c", pid: 3);
        var tracker = new RunTimeTracker();

        // Beim Start läuft a schon: dessen Beginn ist unbekannt.
        Assert.Empty(tracker.Update(Scan(a, b, c), [], T0));
        Assert.Null(tracker.RunningSinceUtc);

        // a fertig → nicht gemessen, aber b startet jetzt.
        Assert.Empty(tracker.Update(Scan(b, c), [a], T0.AddMinutes(5)));
        Assert.Equal(T0.AddMinutes(5), tracker.RunningSinceUtc);

        // b fertig nach 12 min → gemessen.
        var records = tracker.Update(Scan(c), [b], T0.AddMinutes(17));
        var r = Assert.Single(records);
        Assert.Equal("Hb-2", r.Id);
        Assert.Equal(12 * 60, r.Seconds);
        Assert.Equal("Dekor Weiß 19mm", r.Material);
        Assert.Equal(14, r.Parts);
        Assert.Equal(T0.AddMinutes(17), tracker.RunningSinceUtc);
    }

    [Fact]
    public void Tracker_starts_clock_when_job_arrives_at_empty_machine_and_ignores_implausible_times()
    {
        var a = Job("a");
        var tracker = new RunTimeTracker();
        tracker.Update(Scan(), [], T0);
        tracker.Update(Scan(a), [], T0.AddMinutes(1));
        Assert.Equal(T0.AddMinutes(1), tracker.RunningSinceUtc);

        // Über Nacht liegen geblieben: 10 h sind keine Laufzeit.
        Assert.Empty(tracker.Update(Scan(), [a], T0.AddHours(11)));
        Assert.Null(tracker.RunningSinceUtc);
    }

    [Fact]
    public void Tracker_does_not_time_next_job_when_top_was_removed_instead_of_finished()
    {
        var a = Job("a");
        var b = Job("b");
        var tracker = new RunTimeTracker();
        tracker.Update(Scan(), [], T0);
        tracker.Update(Scan(a, b), [], T0);
        Assert.Empty(tracker.Update(Scan(b), [], T0.AddMinutes(3))); // a zurückgeholt, nicht fertig
        Assert.Null(tracker.RunningSinceUtc);
    }

    [Fact]
    public void Controller_writes_run_time_when_machine_finishes_a_board()
    {
        var paths = new AppPaths(_root);
        Directory.CreateDirectory(paths.Input);
        Directory.CreateDirectory(paths.NestResult);
        var now = T0;
        var store = new RunTimeStore(paths.RunTimeFile);
        var controller = new QueueController(paths, new Core.Logging.ActionLog(paths.LogFolder), utcNow: () => now, runTimes: store);

        static void Write(string dir, string key, int stamp)
        {
            File.WriteAllText(Path.Combine(dir, key + ".hop"), $";NCNAME={key}\r\nVARS\r\n   DX := 1000;\r\n   DY := 500;\r\n   DZ := 19;\r\nSTART\r\n");
            File.WriteAllText(Path.Combine(dir, key + ".lbl"), $"[Board]\r\nMaterial=Eiche\r\nDimX=1000\r\nDimY=500\r\nPartsOnBoard=3\r\n[Part_1]\r\nDimX=10\r\nDimY=10\r\n");
            File.SetLastWriteTimeUtc(Path.Combine(dir, key + ".hop"), T0.AddMinutes(stamp));
        }

        controller.Refresh();                       // Maschine leer
        Write(paths.Input, "p1", 1);
        Write(paths.Input, "p2", 2);
        now = T0.AddMinutes(1);
        controller.Refresh();                       // p1 kommt an und läuft
        File.Delete(Path.Combine(paths.Input, "p1.hop"));
        File.Delete(Path.Combine(paths.Input, "p1.lbl"));
        now = T0.AddMinutes(9);
        var snap = controller.Refresh();            // p1 fertig nach 8 min, p2 läuft

        var r = Assert.Single(store.Load());
        Assert.Equal("p1", r.File);
        Assert.Equal(8 * 60, r.Seconds);
        Assert.Equal("Eiche", r.Material);
        Assert.Equal(3, r.Parts);
        Assert.Equal(T0.AddMinutes(9), snap.RunningSinceUtc);
    }

    // ---------- Schätzen ----------

    [Fact]
    public void Estimator_prefers_exact_matches_and_uses_median()
    {
        var est = new RunTimeEstimator([
            Rec("1", 10), Rec("2", 11), Rec("3", 60), // Ausreißer
            Rec("4", 30, material: "Eiche Natur 19mm"),
        ]);

        var e = est.Estimate(Job("x"))!;
        Assert.Equal(EstimateBasis.Exact, e.Basis);
        Assert.Equal(3, e.Samples);
        Assert.Equal(TimeSpan.FromMinutes(11), e.Duration);
    }

    [Fact]
    public void Estimator_scales_by_parts_for_same_board_and_falls_back_to_coarser_groups()
    {
        // Grundzeit 4 min + 1 min je Teil
        var est = new RunTimeEstimator([Rec("1", 8, parts: 4), Rec("2", 12, parts: 8), Rec("3", 16, parts: 12)]);

        var e = est.Estimate(Job("x", parts: 10))!;
        Assert.Equal(EstimateBasis.SameBoard, e.Basis);
        Assert.Equal(TimeSpan.FromMinutes(14), e.Duration);

        var other = est.Estimate(Job("y", material: "MDF roh 8mm", dx: 1000, dy: 500, parts: 4))!;
        Assert.Equal(EstimateBasis.All, other.Basis);

        Assert.Null(new RunTimeEstimator([]).Estimate(Job("z")));
    }

    [Fact]
    public void Estimator_follows_recent_measurements()
    {
        var old = Enumerable.Range(0, 40).Select(i => Rec("old" + i, 20, day: 1));
        var recent = Enumerable.Range(0, RunTimeEstimator.RecentCount).Select(i => Rec("new" + i, 10, day: 5));
        var e = new RunTimeEstimator(old.Concat(recent)).Estimate(Job("x"))!;
        Assert.Equal(TimeSpan.FromMinutes(10), e.Duration);
    }

    [Fact]
    public void Forecast_chains_start_times_and_computes_remaining_time()
    {
        var est = new RunTimeEstimator([Rec("1", 16), Rec("2", 16), Rec("3", 16)]);
        var now = T0.AddMinutes(9);
        var f = QueueForecast.Build([Job("a"), Job("b"), Job("c")], est, T0, now);

        Assert.Equal(TimeSpan.FromMinutes(7), f[0].Remaining);
        Assert.Equal(T0.AddMinutes(16), f[0].EndUtc);
        Assert.Equal(T0.AddMinutes(16), f[1].StartUtc);
        Assert.Equal(T0.AddMinutes(32), f[1].EndUtc);
        Assert.Equal(T0.AddMinutes(48), f[2].EndUtc);

        // Überfällig: gleich fertig, die anderen schieben sich ab jetzt.
        var late = QueueForecast.Build([Job("a"), Job("b")], est, T0, T0.AddMinutes(30));
        Assert.Equal(TimeSpan.Zero, late[0].Remaining);
        Assert.Equal(T0.AddMinutes(46), late[1].EndUtc);

        // Start unbekannt: volle Dauer ab jetzt.
        var unknown = QueueForecast.Build([Job("a")], est, null, now);
        Assert.False(unknown[0].StartKnown);
        Assert.Equal(now.AddMinutes(16), unknown[0].EndUtc);
    }

    // ---------- Speicher und Verbindung ----------

    [Fact]
    public void Store_dedupes_and_returns_cached_list_until_file_grows()
    {
        var store = new RunTimeStore(Path.Combine(_root, "laufzeiten.jsonl"));
        Assert.Equal(2, store.AddRange([Rec("1", 5), Rec("2", 6), Rec("1", 7)]));
        var first = store.Load();
        Assert.Same(first, store.Load());
        Assert.False(store.Add(Rec("2", 9)));
        store.Add(Rec("3", 9));
        Assert.Equal(3, store.Load().Count);
        Assert.Equal(5 * 60, store.Load().Single(r => r.Id == "1").Seconds);
    }

    [Fact]
    public void Statistics_store_still_works_on_shared_base()
    {
        var store = new StatisticsStore(Path.Combine(_root, "statistik.jsonl"));
        var r = BoardRecord.Create("x", T0, "M", 100, 100, 5000, "Job", "f", BoardRecord.SourceMachine)!;
        Assert.True(store.Add(r));
        Assert.False(store.Add(r));
        Assert.True(store.Contains("x"));
        Assert.Single(store.Load());
    }

    [Fact]
    public void Connection_url_from_neon_is_understood()
    {
        Assert.Null(DatabaseConnection.TryBuild(
            "\"postgresql://user_1:geheim%21@ep-test-pooler.eu-central-1.aws.neon.tech/neondb?sslmode=require&channel_binding=require\"",
            out var cs));
        var b = new Npgsql.NpgsqlConnectionStringBuilder(cs);
        Assert.Equal("ep-test-pooler.eu-central-1.aws.neon.tech", b.Host);
        Assert.Equal("neondb", b.Database);
        Assert.Equal("user_1", b.Username);
        Assert.Equal("geheim!", b.Password);
        Assert.Equal(Npgsql.SslMode.Require, b.SslMode);
        Assert.Equal(Npgsql.ChannelBinding.Require, b.ChannelBinding);
        Assert.Equal("user_1@ep-test-pooler.eu-central-1.aws.neon.tech/neondb", DatabaseConnection.Describe(cs));

        // So kopiert Neon die Verbindung oft heraus – mit psql davor oder als Variable.
        foreach (var pasted in new[]
                 {
                     "psql 'postgresql://user_1:geheim%21@ep-test-pooler.eu-central-1.aws.neon.tech/neondb?sslmode=require&channel_binding=require'",
                     "DATABASE_URL=\"postgresql://user_1:geheim%21@ep-test-pooler.eu-central-1.aws.neon.tech/neondb?sslmode=require\"",
                     "  postgresql://user_1:geheim%21@ep-test-pooler.eu-central-1.aws.neon.tech/neondb?sslmode=require\r\n",
                 })
        {
            Assert.Null(DatabaseConnection.TryBuild(pasted, out var pastedCs));
            Assert.Equal("geheim!", new Npgsql.NpgsqlConnectionStringBuilder(pastedCs).Password);
        }

        Assert.NotNull(DatabaseConnection.TryBuild("", out _));
        Assert.NotNull(DatabaseConnection.TryBuild("postgresql://host-ohne-db", out _));
    }
}
