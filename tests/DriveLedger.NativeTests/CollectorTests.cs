using DriveLedger.Core;
using DriveLedger.Windows;

static class CollectorTests
{
    static void Check(bool value, string message)
    {
        if (!value)
            throw new Exception(message);
    }

    static void Wait(Func<bool> condition, [System.Runtime.CompilerServices.CallerArgumentExpression("condition")] string label = "")
    {
        var until = DateTime.UtcNow.AddSeconds(20);
        while (!condition())
        {
            if (DateTime.UtcNow > until)
                throw new TimeoutException(label);
            Thread.Sleep(100);
        }
    }

    public static void Register(List<(string, Action)> tests)
    {
        tests.Add(("routine scan refreshes active writers and retains concurrent journal creations",()=>Fixture((p,s)=> {
            var root=Path.Combine(p,"observed");Directory.CreateDirectory(root);
            for(var i=0;i<60;i++)File.WriteAllText(Path.Combine(root,$"seed{i:D3}"),"fixture");
            var options=new CollectorOptions(FixtureScanRoot:root,BatchSize:1,BatchDelay:TimeSpan.FromMilliseconds(100),JournalInterval:TimeSpan.FromMilliseconds(100),SampleInterval:TimeSpan.FromMilliseconds(100));
            var collector=new Collector(s,options);using var stop=new CancellationTokenSource();var run=Task.Run(()=>collector.RunAsync(stop.Token));
            try {
                Wait(()=>s.ReadCollectorStatus().BaselineReady);
                var start=DateTimeOffset.Now.AddSeconds(-1);
                using var writer=new FileStream(Path.Combine(root,"active"),FileMode.Create,FileAccess.Write,FileShare.ReadWrite|FileShare.Delete);
                writer.Write(new byte[8192]);writer.Flush(true);Wait(()=>s.QueryReport(start,DateTimeOffset.Now).AddedBytes>=8192);
                collector.RequestScan();Wait(()=>s.ReadCollectorStatus().State=="核对中");Thread.Sleep(400);
                writer.Write(new byte[512*1024]);writer.Flush(true);
                var concurrent=Path.Combine(root,"concurrent");File.WriteAllBytes(concurrent,new byte[4096]);
                var deadline=DateTimeOffset.Now.AddSeconds(3);while(s.QueryReport(start,DateTimeOffset.Now).AddedBytes<512*1024&&DateTimeOffset.Now<deadline)Thread.Sleep(50);
                Check(s.QueryReport(start,DateTimeOffset.Now).AddedBytes>=512*1024&&s.ReadCollectorStatus().State=="核对中","active refresh deferred until end of scan");
                Wait(()=>s.ReadCollectorStatus().State=="运行中");Thread.Sleep(150);
                Check(!s.QueryReport(start,DateTimeOffset.Now).Files.Any(x=>x.OldPath==concurrent&&x.Kind=="deleted"),"scan deleted a concurrent journal observation");
            }finally{stop.Cancel();run.GetAwaiter().GetResult();}
        })));
        tests.Add(("active writer growth survives collector restart before close", () => Fixture((p, s) =>
        {
            var root = Path.Combine(p, "observed");
            Directory.CreateDirectory(root);
            var options = new CollectorOptions(FixtureScanRoot: root, JournalInterval: TimeSpan.FromMilliseconds(100), SampleInterval: TimeSpan.FromMilliseconds(100));
            using var stop = new CancellationTokenSource();
            var collector = new Collector(s, options);
            var run = Task.Run(() => collector.RunAsync(stop.Token));
            FileStream? writer = null;
            try
            {
                Wait(() => s.ReadCollectorStatus().BaselineReady);
                writer = new FileStream(Path.Combine(root, "active"), FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                writer.Write(new byte[8192]);
                writer.Flush(true);
                Wait(() => s.QueryReport(DateTimeOffset.Now.AddMinutes(-1), DateTimeOffset.Now).AddedBytes >= 8192);
            }
            finally
            {
                stop.Cancel();
                run.GetAwaiter().GetResult();
            }

            using (writer)
            {
                using var again = new CancellationTokenSource();
                var restarted = new Collector(new MonitorStore(s.DatabasePath), options);
                var task = Task.Run(() => restarted.RunAsync(again.Token));
                try
                {
                    Wait(() => s.ReadCollectorStatus().State == "运行中");
                    writer!.Write(new byte[512 * 1024]);
                    writer.Flush(true);
                    Wait(() => s.QueryReport(DateTimeOffset.Now.AddMinutes(-1), DateTimeOffset.Now).AddedBytes >= 512 * 1024);
                }
                finally
                {
                    again.Cancel();
                    task.GetAwaiter().GetResult();
                }
            }
        })));
        tests.Add(("bounded partial scan preserves unseen files and persists initial coverage", () => Fixture((p, s) =>
        {
            var root = Path.Combine(p, "observed");
            Directory.CreateDirectory(root);
            File.WriteAllBytes(Path.Combine(root, "a"), new byte[4096]);
            var start = DateTimeOffset.Now;
            var old = new FileObservation("unseen", [Path.Combine(root, "unseen")], 123, 123);
            s.ApplyBatch([old], [], start, baseline: true);
            var collector = new Collector(s, new(FixtureScanRoot: root, ScanBudget: TimeSpan.FromTicks(1), JournalInterval: TimeSpan.FromMilliseconds(100)));
            using var end = new CancellationTokenSource();
            var task = Task.Run(() => collector.RunAsync(end.Token));
            try
            {
                Wait(() => s.ReadCollectorStatus().BaselineReady);
            }
            finally
            {
                end.Cancel();
                task.GetAwaiter().GetResult();
            }

            var report = s.QueryReport(start.AddSeconds(-1), DateTimeOffset.Now);
            Check(report.ReleasedBytes == 0, "partial scan inferred deletion");
            Check(report.CoverageNotes.Any(n => n.Code == "initial-baseline" && n.End.HasValue), "baseline interval missing");
            s.ApplyBatch([], ["unseen"], DateTimeOffset.Now);
            s.RecordDiskSample(NativeMetadata.Sample("C:\\"));
            Check(s.QueryReport(start.AddSeconds(-1), DateTimeOffset.Now.AddSeconds(1)).Files.Any(x => x.FileId == "unseen" && x.ReleasedBytes == 123), "retained identity");
        })));
        tests.Add(("journal directory rename migrates unchanged descendants without growth", () => Fixture((p, s) =>
        {
            var root = Path.Combine(p, "observed");
            var old = Path.Combine(root, "old");
            Directory.CreateDirectory(old);
            File.WriteAllBytes(Path.Combine(old, "child"), new byte[4096]);
            var collector = new Collector(s, new(FixtureScanRoot: root, JournalInterval: TimeSpan.FromMilliseconds(100), SampleInterval: TimeSpan.FromMilliseconds(100)));
            using var stop = new CancellationTokenSource();
            var run = Task.Run(() => collector.RunAsync(stop.Token));
            try
            {
                Wait(() => s.ReadCollectorStatus().BaselineReady);
                var start = DateTimeOffset.Now.AddSeconds(-1);
                var renamed = Path.Combine(root, "renamed");
                Directory.Move(old, renamed);
                Wait(() => s.QueryReport(start, DateTimeOffset.Now).Folders.Any(x => x.Path == renamed && x.MigratedInBytes == 4096));
                Check(s.QueryReport(start, DateTimeOffset.Now).AddedBytes == 0, "rename invented growth");
            }
            finally
            {
                stop.Cancel();
                run.GetAwaiter().GetResult();
            }
        })));
        tests.Add(("local authenticated control permits bounded status pause resume rescan only", () => Fixture((p, s) =>
        {
            var collector = new Collector(s, new());
            var name = "DriveLedger.Test." + Guid.NewGuid().ToString("N");
            using var stop = new CancellationTokenSource();
            var server = new ControlPipeServer(collector, s, System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value, name);
            var task = Task.Run(() => server.RunAsync(stop.Token));
            try
            {
                foreach (var command in Enum.GetValues<ControlCommand>())
                {
                    var response = ControlPipeClient.SendAsync(command, default, name).GetAwaiter().GetResult();
                    Check(response.Success, "control failed");
                }

                try
                {
                    ControlPipeClient.SendAsync((ControlCommand)99, default, name).GetAwaiter().GetResult();
                    throw new Exception("invalid command allowed");
                }
                catch (ArgumentOutOfRangeException)
                {
                }
            }
            finally
            {
                stop.Cancel();
                task.GetAwaiter().GetResult();
            }
        })));
        tests.Add(("repair first knowledge preserves unknown past while known deltas and replay survive", () => Fixture((p, s) =>
        {
            var t = DateTimeOffset.Now;
            FileObservation F(string id, long n) => new(id, [@"C:\fixture\" + id], n, n);
            s.ApplyBatch([F("A", 100)], [], t, baseline: true);
            s.ApplyBatch([F("A", 150), F("B", 200)], [], t.AddSeconds(1), true, new("v", "j", 1));
            Check(s.QueryReport(t, t.AddMinutes(1)).AddedBytes == 50, "first knowledge counted as new");
            s.ApplyBatch([F("C", 30)], [], t.AddSeconds(2));
            var reopened = new MonitorStore(s.DatabasePath);
            reopened.Initialize();
            reopened.ApplyBatch([F("B", 250)], [], t.AddSeconds(3), true, new("v", "j", 2));
            reopened.ApplyBatch([F("B", 250)], [], t.AddSeconds(3), true, new("v", "j", 2));
            var r = reopened.QueryReport(t, t.AddMinutes(1));
            Check(r.AddedBytes == 130, "delta/replay");
            Check(r.CoverageNotes.Any(x => x.Code == "first-knowledge"), "missing uncertainty");
        })));
        tests.Add(("collector real fixture baseline create grow rename delete pause and restart", () => Fixture((p, s) =>
        {
            var root = Path.Combine(p, "observed");
            Directory.CreateDirectory(root);
            var f = Path.Combine(root, "a");
            File.WriteAllBytes(f, new byte[4096]);
            var options = new CollectorOptions(FixtureScanRoot: root, SampleInterval: TimeSpan.FromMilliseconds(150), JournalInterval: TimeSpan.FromMilliseconds(100), ScanInterval: TimeSpan.FromHours(1), BatchDelay: TimeSpan.Zero);
            var collector = new Collector(s, options);
            using var stop = new CancellationTokenSource();
            var running = Task.Run(() => collector.RunAsync(stop.Token));
            try
            {
                Wait(() => s.ReadCollectorStatus().BaselineReady);
                var start = DateTimeOffset.Now.AddSeconds(-1);
                var added = Path.Combine(root, "new");
                File.WriteAllBytes(added, new byte[8192]);
                Wait(() => s.QueryReport(start, DateTimeOffset.Now).AddedBytes >= 8192);
                using var writer = new FileStream(added, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                writer.Position = 8192;
                writer.Write(new byte[65536 - 8192]);
                writer.Flush(true);
                Wait(() => s.QueryReport(start, DateTimeOffset.Now).AddedBytes >= 65536);
                writer.Write(new byte[65536]);
                writer.Flush(true);
                Wait(() => s.QueryReport(start, DateTimeOffset.Now).AddedBytes >= 131072);
                writer.Dispose();
                var renamed = Path.Combine(root, "renamed");
                File.Move(added, renamed);
                Wait(() => s.QueryReport(start, DateTimeOffset.Now).Files.Any(x => x.Path == renamed));
                File.Delete(renamed);
                Wait(() => s.QueryReport(start, DateTimeOffset.Now).ReleasedBytes >= 131072);
                collector.Pause();
                Thread.Sleep(250);
                collector.Resume();
                Wait(() => s.QueryReport(start, DateTimeOffset.Now).CoverageNotes.Any(x => x.Code == "paused"));
                var r = s.QueryReport(start, DateTimeOffset.Now);
                Console.WriteLine($"ALLOCATION total {r.AddedBytes}/{r.ReleasedBytes}");
                Check(r.AddedBytes >= 131072 && r.ReleasedBytes == r.AddedBytes, "unexpected fixture allocation totals");
                Console.WriteLine($"MEASURE fixture added={r.AddedBytes}, released={r.ReleasedBytes}, samples={r.Samples.Count}");
            }
            finally
            {
                stop.Cancel();
                running.GetAwaiter().GetResult();
            }

            var prior = s.ReadCheckpoint();
            Check(prior is not null, "journal checkpoint missing");
            using var end = new CancellationTokenSource();
            var restarted = new Collector(new MonitorStore(s.DatabasePath), options);
            var task = Task.Run(() => restarted.RunAsync(end.Token));
            try
            {
                Wait(() => s.ReadCollectorStatus().State == "运行中");
                Thread.Sleep(300);
            }
            finally
            {
                end.Cancel();
                task.GetAwaiter().GetResult();
            }

            Check(s.ReadCheckpoint()!.NextUsn >= prior!.NextUsn, "restart regressed checkpoint");
        })));
    }

    static void Fixture(Action<string, MonitorStore> action)
    {
        var p = Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "collector-fixtures", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(p);
        try
        {
            var s = new MonitorStore(Path.Combine(p, "data", "ledger.db"));
            s.Initialize();
            action(p, s);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(p, true);
        }
    }
}
