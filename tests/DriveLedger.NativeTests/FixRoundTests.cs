using DriveLedger.Core;
using DriveLedger.Windows;
using System.Text.Json.Nodes;

static class FixRoundTests
{
    static void Check(bool value, string message)
    {
        if (!value)
            throw new Exception(message);
    }

    static void Wait(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(6);
        while (!condition())
        {
            if (DateTime.UtcNow > until)
                throw new TimeoutException();
            Thread.Sleep(50);
        }
    }

    static void Fixture(Action<string, MonitorStore> test)
    {
        var root = Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "fix-fixtures", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new MonitorStore(Path.Combine(root, "data", "ledger.db"));
            store.Initialize();
            test(root, store);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(root, true);
        }
    }

    static CollectorOptions Options(string root) => new(FixtureScanRoot: root, JournalInterval: TimeSpan.FromMilliseconds(100), SampleInterval: TimeSpan.FromMilliseconds(100), BatchDelay: TimeSpan.Zero);
    public static void Register(List<(string, Action)> tests)
    {
        FinalIntegrationTests.Register(tests);
        tests.Add(("proof before allocation survives reopen replay and unknown deletion", () => Fixture((p, s) =>
        {
            var at = DateTimeOffset.Now;
            s.ApplyJournalBatch([], [], ["future"], at, new("v", "j", 1));
            var reopened = new MonitorStore(s.DatabasePath);
            reopened.Initialize();
            var file = new FileObservation("future", [@"C:\fixture\future"], 100, 100);
            reopened.ApplyBatch([file], [], at.AddSeconds(1), true);
            Check(reopened.QueryReport(at.AddSeconds(-1), at.AddMinutes(1)).AddedBytes == 100, "proof lost before first allocation");
            reopened.ApplyJournalBatch([file], [], ["future"], at.AddSeconds(2), new("v", "j", 2));
            reopened.ApplyJournalBatch([file], [], ["future"], at.AddSeconds(3), new("v", "j", 2));
            reopened.ApplyJournalBatch([], [], ["second"], at.AddSeconds(4), new("v", "j", 3));
            reopened.ApplyJournalBatch([file with { FileId = "second", AllocatedBytes = 40, LogicalBytes = 40 }], [], ["second"], at.AddSeconds(5), new("v", "j", 4));
            reopened.ApplyJournalBatch([], [], ["unknown-deleted"], at.AddSeconds(6), new("v", "j", 5));
            reopened.ApplyJournalBatch([], ["unknown-deleted"], ["unknown-deleted"], at.AddSeconds(7), new("v", "j", 6));
            var report = reopened.QueryReport(at.AddSeconds(-1), at.AddMinutes(1));
            Check(report.AddedBytes == 140 && report.ReleasedBytes == 0, "proof replay or unknown deletion invented allocation");
            reopened.Prune(at.AddDays(366));
            using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={s.DatabasePath}");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM creation_provenance";
            Check((long)command.ExecuteScalar()! == 0, "proof-only retention unbounded");
        })));
        tests.Add(("deleted creation provenance is retained for evidence then expires after 365 days", () => Fixture((p, s) =>
        {
            var t = DateTimeOffset.Now;
            var f = new FileObservation("pending", [@"C:\fixture\pending"], 12, 12);
            s.ApplyBatch([f], [], t, true);
            s.ApplyBatch([], [f.FileId], t.AddSeconds(1));
            long Count()
            {
                using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={s.DatabasePath}");
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT COUNT(*) FROM creation_provenance";
                return (long)command.ExecuteScalar()!;
            }

            Check(Count() == 1, "deleted provenance lost prematurely");
            s.Prune(t.AddDays(364));
            Check(Count() == 1, "provenance retention too short");
            s.Prune(t.AddDays(366));
            Check(Count() == 0, "provenance retention unbounded");
        })));
        tests.Add(("active state persistence failure remains fatal rather than native-race recovery", () => Fixture((p, s) =>
        {
            var root = Path.Combine(p, "observed");
            Directory.CreateDirectory(root);
            using var stop = new CancellationTokenSource();
            var collector = new Collector(s, Options(root));
            var task = Task.Run(() => collector.RunAsync(stop.Token));
            try
            {
                Wait(() => s.ReadCollectorStatus().BaselineReady);
                Directory.CreateDirectory(s.DatabasePath + ".active.json.tmp");
                using var writer = new FileStream(Path.Combine(root, "active"), FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                writer.Write(new byte[4096]);
                writer.Flush(true);
                Wait(() => task.IsCompleted);
                try
                {
                    task.GetAwaiter().GetResult();
                    throw new Exception("persistence failure swallowed");
                }
                catch (Exception e)when (e is IOException or UnauthorizedAccessException)
                {
                }

                Check(s.ReadCollectorStatus().State == "错误", "persistence failure not visible");
            }
            finally
            {
                stop.Cancel();
                try
                {
                    task.GetAwaiter().GetResult();
                }
                catch (Exception e)when (e is IOException or UnauthorizedAccessException)
                {
                }
            }
        })));
        tests.Add(("native creation deliberately scanned before journal proof is attributed exactly once", () => Fixture((p, s) =>
        {
            var root = Path.Combine(p, "observed");
            Directory.CreateDirectory(root);
            using var journal = new UsnJournal("C:\\");
            var state = journal.Query();
            var file = Path.Combine(root, "created");
            File.WriteAllBytes(file, new byte[4096]);
            var observed = NativeMetadata.Walk(root, e => throw e, CancellationToken.None).Single(x => !x.IsDirectory);
            var first = DateTimeOffset.Now;
            s.ApplyBatch([observed], [], first, true, scanId: "deliberate-scan-first");
            Check(s.QueryReport(first.AddSeconds(-1), DateTimeOffset.Now).AddedBytes == 0, "test did not establish scan-first state");
            var next = state.NextUsn;
            var proof = false;
            while (next < journal.Query().NextUsn)
            {
                var page = journal.Read(next, state.JournalId);
                if (page.NextUsn <= next)
                    break;
                var ids = page.Records.Where(x => (x.Reason & 0x80000100) == 0x100 && journal.FileId(x.FileReference) == observed.FileId).Select(x => observed.FileId).Distinct().ToArray();
                if (ids.Length > 0)
                    proof = true;
                s.ApplyJournalBatch(ids.Length > 0 ? [NativeMetadata.Read(file)] : [], [], ids, DateTimeOffset.Now, new(journal.VolumeId, state.JournalId.ToString(), page.NextUsn));
                next = page.NextUsn;
                if (proof)
                    break;
            }

            Check(proof, "real native creation evidence missing");
            s.ApplyJournalBatch([observed], [], [observed.FileId], DateTimeOffset.Now, new(journal.VolumeId, state.JournalId.ToString(), next));
            Check(s.QueryReport(first.AddSeconds(-1), DateTimeOffset.Now).AddedBytes == observed.AllocatedBytes, "native scan-first creation not counted once");
        })));
        tests.Add(("initial baseline held writer close summary never creates new allocation", () => Fixture((p, s) =>
        {
            var root = Path.Combine(p, "observed");
            Directory.CreateDirectory(root);
            var writer = new FileStream(Path.Combine(root, "existing"), FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            writer.Write(new byte[4096]);
            writer.Flush(true);
            using var stop = new CancellationTokenSource();
            var collector = new Collector(s, Options(root));
            var run = Task.Run(() => collector.RunAsync(stop.Token));
            try
            {
                Wait(() => s.ReadCollectorStatus().BaselineReady);
                writer.Dispose();
                Thread.Sleep(400);
                Check(s.QueryReport(DateTimeOffset.Now.AddMinutes(-1), DateTimeOffset.Now).AddedBytes == 0, "CLOSE promoted preexisting baseline");
            }
            finally
            {
                writer.Dispose();
                stop.Cancel();
                run.GetAwaiter().GetResult();
            }
        })));
        foreach (var currentBoot in new string? []
        {
            "test-boot-a",
            "test-boot-b",
            null
        }

        )
            tests.Add(($"active degradation boot recovery requires proven session change ({currentBoot ?? "unavailable"})", () => Fixture((p, s) =>
            {
                var root = Path.Combine(p, "observed");
                Directory.CreateDirectory(root);
                void Run(string? boot, bool? degraded)
                {
                    using var stop = new CancellationTokenSource();
                    var collector = new Collector(s, Options(root), (j, id) => j.Resolve(id), () => boot);
                    var task = Task.Run(() => collector.RunAsync(stop.Token));
                    try
                    {
                        Wait(() => s.ReadCollectorStatus().State is "运行中" or "降级运行");
                        Thread.Sleep(200);
                        if (degraded.HasValue)
                            Check((s.ReadCollectorStatus().Message?.Contains("active-set") == true) == degraded.Value, "invalid boot continuity transition");
                    }
                    finally
                    {
                        stop.Cancel();
                        task.GetAwaiter().GetResult();
                    }
                }

                Run("test-boot-a", null);
                var path = s.DatabasePath + ".active.json";
                var state = JsonNode.Parse(File.ReadAllText(path))!;
                state["DegradedSince"] = DateTimeOffset.Now.AddSeconds(-1).ToString("O");
                File.WriteAllText(path, state.ToJsonString());
                Run(currentBoot, currentBoot != "test-boot-b");
                if (currentBoot == "test-boot-b")
                    Check(s.QueryReport(DateTimeOffset.Now.AddMinutes(-1), DateTimeOffset.Now).CoverageNotes.Any(x => x.Code == "active-set-recovered"), "recovery evidence missing");
            })));
        tests.Add(("creation proof after scan-first observation counts initial allocation once across replay and deletion", () => Fixture((p, s) =>
        {
            var t = DateTimeOffset.Now;
            var f = new FileObservation("late", [@"C:\fixture\late"], 100, 100);
            s.ApplyBatch([f], [], t, true, scanId: "repair");
            var reopened = new MonitorStore(s.DatabasePath);
            reopened.Initialize();
            reopened.ApplyJournalBatch([f with { AllocatedBytes = 150, LogicalBytes = 150 }], [], ["late"], t.AddSeconds(1), new("v", "j", 1));
            reopened.ApplyJournalBatch([f], [], ["late"], t.AddSeconds(2), new("v", "j", 1));
            Check(reopened.QueryReport(t.AddSeconds(-1), t.AddMinutes(1)).AddedBytes == 150, "scan consumed creation provenance");
            var gone = f with
            {
                FileId = "gone",
                Paths = [@"C:\fixture\gone"],
                AllocatedBytes = 40,
                LogicalBytes = 40
            };
            reopened.ApplyBatch([gone], [], t.AddSeconds(3), true, scanId: "repair");
            reopened.ApplyBatch([], ["gone"], t.AddSeconds(4));
            var again = new MonitorStore(s.DatabasePath);
            again.Initialize();
            again.ApplyJournalBatch([], ["gone"], ["gone"], t.AddSeconds(5), new("v", "j", 2));
            again.ApplyJournalBatch([], [], ["gone"], t.AddSeconds(6), new("v", "j", 3));
            var r = again.QueryReport(t.AddSeconds(-1), t.AddMinutes(1));
            Check(r.AddedBytes == 190 && r.ReleasedBytes == 40, "deleted provenance lost or replayed");
            Check(r.Files.Where(x => x.Kind == "creation-confirmed").All(x => x.At >= t.AddSeconds(1)), "proof retroactively dated");
        })));
        tests.Add(("ordinary native IOException preserves collection and independent sampling", () => Fixture((p, s) =>
        {
            var root = Path.Combine(p, "observed");
            Directory.CreateDirectory(root);
            var fail = 1;
            var collector = new Collector(s, Options(root), (j, id) =>
            {
                var item = j.Resolve(id);
                if (item.Paths.Any(x => x.EndsWith("racy")) && Interlocked.Exchange(ref fail, 0) == 1)
                    throw new IOException("owned fixture metadata replacement race");
                return item;
            });
            using var stop = new CancellationTokenSource();
            var task = Task.Run(() => collector.RunAsync(stop.Token));
            try
            {
                Wait(() => s.ReadCollectorStatus().BaselineReady);
                var outside = Path.Combine(p, "outside-observed-root");
                File.WriteAllBytes(outside, new byte[4096]);
                var outsideId = NativeMetadata.Read(outside).FileId;
                File.WriteAllBytes(Path.Combine(root, "racy"), new byte[4096]);
                Wait(() => Volatile.Read(ref fail) == 0);
                Thread.Sleep(400);
                Check(!task.IsCompleted, "metadata race terminated collector");
                var r = s.QueryReport(DateTimeOffset.Now.AddMinutes(-1), DateTimeOffset.Now);
                Check(r.CoverageNotes.Any(x => x.Code == "metadata-unavailable"), "race coverage absent");
                Check(r.Samples.Count >= 3, "sampler stopped");
                Check(r.AddedBytes == 4096, "recovered first allocation lost after native race");
                using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={s.DatabasePath}");
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT COUNT(*) FROM creation_provenance WHERE id=$id";
                command.Parameters.AddWithValue("$id", outsideId);
                Check((long)command.ExecuteScalar()! == 0, "proof-only identity escaped fixture boundary");
            }
            finally
            {
                stop.Cancel();
                task.GetAwaiter().GetResult();
            }

            var observation = NativeMetadata.Read(Path.Combine(root, "racy"));
            var checkpoint = s.ReadCheckpoint()!;
            s.ApplyJournalBatch([observation], [], [observation.FileId], DateTimeOffset.Now, checkpoint);
            using var restartedStop = new CancellationTokenSource();
            var restarted = new Collector(new MonitorStore(s.DatabasePath), Options(root));
            var restartedTask = Task.Run(() => restarted.RunAsync(restartedStop.Token));
            try
            {
                Wait(() => s.ReadCollectorStatus().State == "运行中");
                Thread.Sleep(200);
                Check(s.QueryReport(DateTimeOffset.Now.AddMinutes(-1), DateTimeOffset.Now).AddedBytes == 4096, "recovered allocation duplicated after restart/replay");
            }
            finally
            {
                restartedStop.Cancel();
                restartedTask.GetAwaiter().GetResult();
            }
        })));
        foreach (var corruption in new[]
        {
            "missing",
            "null-references",
            "absent-references",
            "invalid-json"
        }

        )
            tests.Add(($"active continuity remains degraded after {corruption} and repair scan", () => Fixture((p, s) =>
            {
                var root = Path.Combine(p, "observed");
                Directory.CreateDirectory(root);
                var options = Options(root);
                using var writer = new FileStream(Path.Combine(root, "held"), FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                using (var stop = new CancellationTokenSource())
                {
                    var collector = new Collector(s, options);
                    var run = Task.Run(() => collector.RunAsync(stop.Token));
                    try
                    {
                        Wait(() => s.ReadCollectorStatus().BaselineReady);
                        writer.Write(new byte[8192]);
                        writer.Flush(true);
                        Thread.Sleep(300);
                    }
                    finally
                    {
                        stop.Cancel();
                        run.GetAwaiter().GetResult();
                    }
                }

                var sidecar = s.DatabasePath + ".active.json";
                Wait(() => File.Exists(sidecar));
                if (corruption == "missing")
                    File.Delete(sidecar);
                else if (corruption == "invalid-json")
                    File.WriteAllText(sidecar, "{");
                else
                {
                    var json = JsonNode.Parse(File.ReadAllText(sidecar))!;
                    if (corruption == "null-references")
                        json["References"] = null;
                    else
                        json.AsObject().Remove("References");
                    File.WriteAllText(sidecar, json.ToJsonString());
                }

                for (var restart = 0; restart < 2; restart++)
                {
                    using var stop = new CancellationTokenSource();
                    var collector = new Collector(s, options);
                    var run = Task.Run(() => collector.RunAsync(stop.Token));
                    try
                    {
                        Wait(() => s.ReadCollectorStatus().State is "运行中" or "降级运行" || run.IsCompleted);
                        if (run.IsCompleted)
                            run.GetAwaiter().GetResult();
                        collector.RequestScan();
                        Thread.Sleep(400);
                        writer.Write(new byte[4096]);
                        writer.Flush(true);
                        Thread.Sleep(200);
                        Check(s.ReadCollectorStatus().Message?.Contains("active-set") == true, "active continuity silently healthy");
                        Check(s.QueryReport(DateTimeOffset.Now.AddMinutes(-1), DateTimeOffset.Now).CoverageNotes.Any(x => x.Code == "active-set-unavailable"), "continuity gap absent");
                    }
                    finally
                    {
                        stop.Cancel();
                        run.GetAwaiter().GetResult();
                    }
                }
            })));
    }
}
