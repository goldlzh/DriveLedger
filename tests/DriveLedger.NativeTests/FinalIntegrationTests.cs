using DriveLedger.Core;
using DriveLedger.Windows;
using System.Reflection;
using System.Text.Json.Nodes;

static class FinalIntegrationTests
{
    static void Check(bool v, string m) { if (!v) throw new Exception(m); }
    static void Wait(Func<bool> f, double seconds = 8) { var end = DateTime.UtcNow.AddSeconds(seconds); while (!f()) { if (DateTime.UtcNow > end) throw new TimeoutException(); Thread.Sleep(30); } }
    static void Fixture(Action<string, MonitorStore> test)
    {
        var p = Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "final-fixtures", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(Path.Combine(p, "root"));
        try { var s = new MonitorStore(Path.Combine(p, "data", "ledger.db")); s.Initialize(); test(Path.Combine(p, "root"), s); }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(p, true); }
    }
    static string? Sql(MonitorStore s, string sql) { using var c = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={s.DatabasePath}"); c.Open(); using var cmd = c.CreateCommand(); cmd.CommandText = sql; return cmd.ExecuteScalar()?.ToString(); }
    static CollectorOptions Options(string p) => new(FixtureScanRoot: p, JournalInterval: TimeSpan.FromMilliseconds(50), SampleInterval: TimeSpan.FromMilliseconds(50), ScanInterval: TimeSpan.FromSeconds(3), BatchDelay: TimeSpan.Zero);
    static void Run(Collector c, Action action) { using var stop = new CancellationTokenSource(); var task = Task.Run(() => c.RunAsync(stop.Token)); try { action(); } finally { stop.Cancel(); task.GetAwaiter().GetResult(); } }
    public static void Register(List<(string, Action)> tests)
    {
        tests.Add(("routine reconciliation survives successive restarts and missed deadline", () => Fixture((p, s) =>
        {
            File.WriteAllBytes(Path.Combine(p, "marker"), new byte[4096]);
            Run(new Collector(s, Options(p)), () => Wait(() => s.ReadCollectorStatus().BaselineReady));
            var initial = Sql(s, "SELECT scan FROM files LIMIT 1");
            for (var i = 0; i < 2; i++) Run(new Collector(s, Options(p)), () => { Wait(() => s.ReadCollectorStatus().State == "运行中"); Thread.Sleep(150); Check(initial == Sql(s, "SELECT scan FROM files LIMIT 1"), "early routine scan"); });
            Thread.Sleep(3200);
            Run(new Collector(s, Options(p)), () => Wait(() => Sql(s, "SELECT scan FROM files LIMIT 1") != initial, 1));
        })));
        tests.Add(("legacy schedule reconciles immediately and incomplete scan never advances completion", () => Fixture((p, s) =>
        {
            File.WriteAllBytes(Path.Combine(p, "marker"), new byte[4096]);
            Run(new Collector(s, Options(p)), () => Wait(() => s.ReadCollectorStatus().BaselineReady));
            var prior = Sql(s, "SELECT scan FROM files LIMIT 1");
            Sql(s, "DELETE FROM meta WHERE key IN ('lastCompletedScan','nextScanAttemptAt')");
            Run(new Collector(s, Options(p)), () => Wait(() => Sql(s, "SELECT scan FROM files LIMIT 1") != prior, 1));
            var completed = Sql(s, "SELECT value FROM meta WHERE key='lastCompletedScan'");
            Check(completed is not null, "completion not durable");
            s.CompleteScan("partial", DateTimeOffset.Now.AddDays(1), false, false);
            Check(completed == Sql(s, "SELECT value FROM meta WHERE key='lastCompletedScan'"), "partial scan marked complete");
            var attempt = DateTimeOffset.Now;
            var c = new Collector(s, Options(p) with { ScanBudget = TimeSpan.FromTicks(1) }); c.RequestScan();
            Run(c, () => Wait(() => s.QueryReport(DateTimeOffset.Now.AddMinutes(-1), DateTimeOffset.Now).CoverageNotes.Any(x => x.Code == "incomplete-scan" && x.Start >= attempt)));
            Check(completed == Sql(s, "SELECT value FROM meta WHERE key='lastCompletedScan'"), "budget scan marked complete");
            var cancelled = new Collector(s, Options(p) with { BatchSize = 1, BatchDelay = TimeSpan.FromSeconds(2) }); cancelled.RequestScan();
            Run(cancelled, () => Wait(() => s.ReadCollectorStatus().State == "核对中"));
            Check(completed == Sql(s, "SELECT value FROM meta WHERE key='lastCompletedScan'"), "cancelled scan marked complete");
        })));
        tests.Add(("partial routine attempts respect durable interval across restart and eventually retry", () => Fixture((p, s) =>
        {
            File.WriteAllBytes(Path.Combine(p, "marker"), new byte[4096]);
            var options = Options(p) with { ScanInterval = TimeSpan.FromSeconds(70), ScanBudget = TimeSpan.FromTicks(1) };
            long Attempts() => long.Parse(Sql(s, "SELECT COUNT(*) FROM coverage WHERE json_extract(payload,'$.Code')='incomplete-scan'")!);
            var collector = new Collector(s, options);
            long attempts = 0;
            Run(collector, () =>
            {
                Wait(() => s.ReadCollectorStatus().BaselineReady);
                attempts = Attempts();
                Check(attempts > 0, "partial attempt was not exercised");
                Thread.Sleep(61500); // Beyond the previous one-minute automatic retry threshold.
                Check(Attempts() == attempts, "partial scan retried before configured interval");
            });
            Check(s.ReadLastCompletedScan() is null, "partial attempt invented successful completion");
            Run(new Collector(s, options), () =>
            {
                Wait(() => s.ReadCollectorStatus().State == "运行中");
                Thread.Sleep(300);
                Check(Attempts() == attempts, "restart evaded retry bound");
                Wait(() => Attempts() > attempts, 12);
            });
            Check(s.ReadLastCompletedScan() is null, "retry invented successful completion");
        })));
        tests.Add(("cancelled scan attempt is bounded across restart without claiming completion", () => Fixture((p, s) =>
        {
            File.WriteAllBytes(Path.Combine(p, "marker"), new byte[4096]);
            var options = Options(p) with { ScanInterval = TimeSpan.FromSeconds(3), BatchSize = 1, BatchDelay = TimeSpan.FromSeconds(2) };
            Run(new Collector(s, options), () => Wait(() => s.ReadCollectorStatus().State == "核对中"));
            Check(s.ReadLastCompletedScan() is null, "cancelled attempt completed");
            Run(new Collector(s, options with { BatchDelay = TimeSpan.Zero }), () =>
            {
                Thread.Sleep(300);
                Check(s.ReadLastCompletedScan() is null, "restart immediately retried cancelled scan");
                Wait(() => s.ReadLastCompletedScan() is not null, 5);
            });
        })));
        foreach (var trigger in new[] { "journal-loss", "overflow", "retry-overflow" })
            tests.Add(($"runtime {trigger} retains degraded coverage after omitted writer growth repair and restart", () => Fixture((p, s) =>
            {
                var armed = 0; Collector? c = null;
                FileObservation Observe(UsnJournal j, ulong id)
                {
                    if (id >= ulong.MaxValue - 4096) return new(id.ToString(), [], 0, 0);
                    var item = j.Resolve(id);
                    if (item.Paths.Any(x => x == Path.Combine(p, "held")) && Interlocked.Exchange(ref armed, 0) == 1)
                    {
                        if (trigger == "journal-loss") throw new NotSupportedException("injected lost runtime journal page");
                        var dirty = (HashSet<ulong>)typeof(Collector).GetField("dirty", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(c)!;
                        dirty.Clear();
                        for (ulong n = 0; n < 4096; n++) dirty.Add(ulong.MaxValue - n);
                        if (trigger == "retry-overflow") throw new IOException("injected retry overflow");
                    }
                    return item;
                }
                c = new Collector(s, Options(p) with { ScanInterval = TimeSpan.FromHours(1) }, Observe, () => "boot-a");
                using var writer = new FileStream(Path.Combine(p, "held"), FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                Run(c, () =>
                {
                    Wait(() => s.ReadCollectorStatus().BaselineReady); Volatile.Write(ref armed, 1); writer.Write(new byte[8192]); writer.Flush(true);
                    Wait(() => Volatile.Read(ref armed) == 0); Thread.Sleep(300);
                    Check(s.ReadCollectorStatus().State == "降级运行", "runtime loss reported healthy");
                    var active = JsonNode.Parse(File.ReadAllText(s.DatabasePath + ".active.json"))!;
                    Check(active["DegradedSince"] is not null, "degradation not durable");
                    using var journal = new UsnJournal("C:\\");
                    var heldId = NativeMetadata.Read(Path.Combine(p, "held")).FileId;
                    Check(!active["References"]!.AsArray().Any(x => journal.FileId(x!.GetValue<ulong>()) == heldId), "fixture writer was not omitted");
                });
                var later = DateTimeOffset.Now;
                for (var restart = 0; restart < 2; restart++)
                {
                    c = new Collector(s, Options(p) with { ScanInterval = TimeSpan.FromHours(1) }, Observe, () => "boot-a"); c.RequestScan();
                    Run(c, () => { Wait(() => s.ReadCollectorStatus().State == "降级运行"); Thread.Sleep(200); writer.Write(new byte[8192]); writer.Flush(true); Thread.Sleep(200); Check(s.ReadCollectorStatus().Message?.Contains("active-set") == true, "repair cleared uncertainty"); });
                }
                Check(s.QueryReport(later, DateTimeOffset.Now).CoverageNotes.Any(x => x.Code == "active-set-unavailable"), "later interval falsely healthy");
            })));
    }
}
