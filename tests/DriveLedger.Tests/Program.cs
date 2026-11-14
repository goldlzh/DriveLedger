using DriveLedger.Core;

var failed = 0;
var tests = new List<(string, Action<MonitorStore>)>();
var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.FromHours(8));
FileObservation F(string id, long? size, params string[] paths) => new(id, paths, size, size);
ReportSnapshot Report(MonitorStore s) => s.QueryReport(start, start.AddDays(100));
void Eq<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"expected {expected}, got {actual}"); }
void Baseline(MonitorStore s) => s.ApplyBatch([F("A", 100, @"C:\old\a")], [], start, baseline: true, scanId: "base");
foreach (var (total, free, expected) in new[] { (1500L,900L,0L), (1500L,850L,50L), (800L,200L,0L), (800L,250L,-50L) })
    tests.Add(($"capacity change {total}/{free} uses occupied bytes and honest residual", s => {
        Baseline(s);
        s.RecordDiskSample(new(start,"vol",1000,400));
        s.ApplyBatch([F("A",125,@"C:\old\a")],[],start.AddSeconds(1));
        s.RecordDiskSample(new(start.AddSeconds(2),"vol",total,free));
        var r=Report(s); Eq(expected,r.DiskDeltaBytes); Eq(25L,r.AddedBytes); Eq(0L,r.ReleasedBytes); Eq(expected-25,r.UnattributedBytes);
    }));
tests.Add(("baseline existing allocation gives zero growth", s => { Baseline(s); Eq(0L, Report(s).AddedBytes); }));
tests.Add(("growth and new allocation total 70; replay checkpoint is idempotent", s =>
{
    Baseline(s); var cp = new JournalCheckpoint("vol", "journal", 10);
    s.ApplyBatch([F("A", 150, @"C:\old\a"), F("B", 20, @"C:\old\b")], [], start.AddHours(1), cp);
    s.ApplyBatch([F("A", 150, @"C:\old\a"), F("B", 20, @"C:\old\b")], [], start.AddHours(1), cp);
    Eq(70L, Report(s).AddedBytes); Eq(cp, s.ReadCheckpoint());
}
));
tests.Add(("deletion releases last known allocation", s => { Baseline(s); s.ApplyBatch([], ["A"], start.AddHours(1)); Eq(100L, Report(s).ReleasedBytes); }));
tests.Add(("moving unchanged allocation migrates without new bytes", s => { Baseline(s); s.ApplyBatch([F("A", 100, @"C:\new\a")], [], start.AddHours(1)); var r = Report(s); Eq(0L, r.NetBytes); Eq(100L, r.Folders.Single(x => x.Path == @"C:\new").MigratedInBytes); }));
tests.Add(("two hard links count one allocation with deterministic canonical path", s => { s.ApplyBatch([F("A", 100, @"C:\z\a", @"C:\a\b")], [], start.AddHours(1)); Eq(100L, Report(s).AddedBytes); Eq(@"C:\a\b", Report(s).Files.Single().Path); }));
tests.Add(("unknown allocation preserves previous size", s => { Baseline(s); s.ApplyBatch([F("A", null, @"C:\old\a")], [], start.AddHours(1)); s.ApplyBatch([], ["A"], start.AddHours(2)); Eq(100L, Report(s).ReleasedBytes); Eq(true, Report(s).CoverageNotes.Any(x => x.Code == "allocation-unavailable")); }));
tests.Add(("incomplete reconciliation preserves unseen files", s => { Baseline(s); s.CompleteScan("partial", start.AddHours(1), false, false); Eq(0L, Report(s).ReleasedBytes); s.ApplyBatch([], ["A"], start.AddHours(2)); Eq(100L, Report(s).ReleasedBytes); }));
tests.Add(("complete reconciliation deletes only unseen identities", s => { Baseline(s); s.ApplyBatch([F("B", 20, @"C:\b")], [], start.AddHours(1), scanId: "full"); s.CompleteScan("full", start.AddHours(2), true, false); Eq(100L, Report(s).ReleasedBytes); }));
tests.Add(("disk endpoints preserve multi-day gap and offset", s => { s.RecordDiskSample(new(start, "vol", 1000, 900)); s.RecordDiskSample(new(start.AddDays(3), "vol", 1000, 700)); var r = Report(s); Eq(200L, r.DiskDeltaBytes); Eq(TimeSpan.FromDays(3), r.LastObservation!.At - r.FirstObservation!.At); Eq(TimeSpan.FromHours(8), r.FirstObservation.At.Offset); Eq(true, r.CoverageNotes.Any(x => x.Code == "sample-gap")); }));
tests.Add(("one disk sample has unknown delta", s => { s.RecordDiskSample(new(start, "vol", 1000, 900)); Eq((long?)null, Report(s).DiskDeltaBytes); }));
tests.Add(("31-day directory history survives file pruning", s => { s.ApplyBatch([F("A", 100, @"C:\old\a")], [], start.AddHours(1)); s.Prune(start.AddDays(32)); var r = Report(s); Eq(100L, r.Folders.Single(x => x.Path == @"C:\old").AddedBytes); Eq(0, r.Files.Count); Eq(false, r.FileDetailsComplete); s.ApplyBatch([], ["A"], start.AddDays(33)); Eq(100L, Report(s).ReleasedBytes); }));
tests.Add(("directory rename migrates descendants without growth", s => { s.ApplyBatch([new("D", [@"C:\old"], 0, 0, true), F("A", 100, @"C:\old\sub\a")], [], start, baseline: true); s.ApplyBatch([new("D", [@"C:\new"], 0, 0, true)], [], start.AddHours(1)); var r = Report(s); Eq(0L, r.NetBytes); Eq(100L, r.Folders.Single(x => x.Path == @"C:\new").MigratedInBytes); s.ApplyBatch([], ["A"], start.AddHours(2)); Eq(@"C:\new\sub\a", Report(s).Files.Last().OldPath); }));
tests.Add(("status and coverage survive reopening", s => { s.SetCollectorStatus(new("运行中", true, 4, 2)); s.AddCoverageGap("denied", "test", start); Eq(4L, s.ReadCollectorStatus().FilesObserved); Eq(true, Report(s).CoverageNotes.Any(x => x.Code == "denied")); }));
tests.Add(("transaction rolls back index and checkpoint on invalid batch", s => { Baseline(s); try { s.ApplyBatch([F("B", 20, @"C:\b"), F("bad", -1, @"C:\bad")], [], start.AddHours(1), new JournalCheckpoint("v", "j", 20)); } catch (ArgumentException) { } Eq((JournalCheckpoint?)null, s.ReadCheckpoint()); Eq(0L, Report(s).AddedBytes); }));
tests.Add(("unknown baseline allocation becomes known without invented growth", s => { s.ApplyBatch([F("U", null, @"C:\u")], [], start, baseline: true); s.ApplyBatch([F("U", 100, @"C:\u")], [], start.AddHours(1)); Eq(0L, Report(s).AddedBytes); }));
tests.Add(("file changes retain all links and previous real observation time", s => { Baseline(s); s.ApplyBatch([F("A", 150, @"C:\old\a", @"C:\z\link")], [], start.AddDays(3)); var f = Report(s).Files.Single(); Eq(start, f.PreviousObservedAt); Eq(2, f.Paths?.Count ?? 0); }));
tests.Add(("read-only missing database creates no directory", s => { var parent = Path.Combine(Path.GetDirectoryName(s.DatabasePath)!, "missing"); var read = new MonitorStore(Path.Combine(parent, "missing.db"), true); try { read.Initialize(); throw new Exception("missing DB accepted"); } catch (Microsoft.Data.Sqlite.SqliteException) { } Eq(false, Directory.Exists(parent)); }));
tests.Add(("read-only database can query reopened durable index and checkpoint", s => { Baseline(s); s.ApplyBatch([F("A", 150, @"C:\old\a")], [], start.AddHours(1), new JournalCheckpoint("v", "j", 9)); var read = new MonitorStore(s.DatabasePath, true); read.Initialize(); Eq(50L, Report(read).AddedBytes); Eq(9L, read.ReadCheckpoint()!.NextUsn); try { read.RecordDiskSample(new(start, "v", 1000, 100)); throw new Exception("write accepted"); } catch (InvalidOperationException) { } }));
tests.Add(("concurrent indexing sampling and readers remain consistent", s => { Parallel.For(0, 30, i => { s.ApplyBatch([F("p" + i, 10, @"C:\parallel\" + i)], [], start.AddMinutes(i + 1)); s.RecordDiskSample(new(start.AddMinutes(i), "v", 1000, 900 - i)); s.QueryReport(start, start.AddMinutes(30)); }); s.RecordDiskSample(new(start.AddMinutes(31), "v", 1000, 600)); Eq(300L, s.QueryReport(start, start.AddMinutes(31)).AddedBytes); }));
tests.Add(("CSV escapes quotes commas newlines and formula prefixes", s => { Eq("\"a,\"\"b\"\"\n\"", Accounting.CsvCell("a,\"b\"\n")); Eq("\"'=SUM(A1)\"", Accounting.CsvCell("=SUM(A1)")); Eq("1 KiB", Accounting.DisplayBytes(1024)); }));
tests.Add(("retention expires directory samples after 365 days but preserves current index", s => { s.ApplyBatch([F("A", 100, @"C:\a")], [], start.AddHours(1)); s.RecordDiskSample(new(start, "v", 1000, 900)); s.Prune(start.AddDays(366)); Eq(0, Report(s).Folders.Count); Eq(0, Report(s).Samples.Count); s.ApplyBatch([], ["A"], start.AddDays(367)); Eq(100L, s.QueryReport(start.AddDays(366), start.AddDays(368)).ReleasedBytes); }));
tests.Add(("disk delta refuses cross-volume comparison", s => { s.RecordDiskSample(new(start, "v1", 1000, 900)); s.RecordDiskSample(new(start.AddMinutes(1), "v2", 1000, 700)); Eq((long?)null, Report(s).DiskDeltaBytes); Eq(true, Report(s).CoverageNotes.Any(x => x.Code == "volume-change")); }));
tests.Add(("checkpoint replay cannot revert newer indexed size", s => { Baseline(s); s.ApplyBatch([F("A", 150, @"C:\old\a")], [], start.AddMinutes(1), new JournalCheckpoint("v", "j", 10)); s.ApplyBatch([F("A", 100, @"C:\old\a")], [], start.AddMinutes(2), new JournalCheckpoint("v", "j", 9)); Eq(50L, Report(s).AddedBytes); Eq(0L, Report(s).ReleasedBytes); }));
tests.Add(("failed allocation read preserves last successful allocation time", s => {
    Baseline(s);s.ApplyBatch([F("A",null,@"C:\old\a")],[],start.AddDays(1));
    s.ApplyBatch([F("A",150,@"C:\old\a")],[],start.AddDays(3));Eq(start,Report(s).Files.Last().PreviousObservedAt);
}));
tests.Add(("directory path migration does not advance descendant allocation time", s => {
    s.ApplyBatch([new("D",[@"C:\old"],0,0,true),F("A",100,@"C:\old\a")],[],start,baseline:true);
    s.ApplyBatch([new("D",[@"C:\new"],0,0,true)],[],start.AddDays(1));
    s.ApplyBatch([F("A",150,@"C:\new\a")],[],start.AddDays(3));Eq(start,Report(s).Files.Last().PreviousObservedAt);
}));
tests.Add(("pathless allocation stays in durable volume totals through resolution deletion pruning", s => {
    s.ApplyBatch([F("U",100)],[],start.AddHours(1));Eq(100L,Report(s).AddedBytes);
    Eq(true,Report(s).CoverageNotes.Any(x=>x.Code=="path-unavailable"));
    s.ApplyBatch([F("U",100,@"C:\resolved\u")],[],start.AddHours(2));Eq(100L,Report(s).AddedBytes);
    Eq(100L,Report(s).Folders.Single(x=>x.Path==@"C:\resolved").MigratedInBytes);
    s.ApplyBatch([], ["U"],start.AddHours(3));Eq(100L,Report(s).ReleasedBytes);
    s.Prune(start.AddDays(32));Eq(100L,Report(s).AddedBytes);Eq(100L,Report(s).ReleasedBytes);
}));
tests.Add(("disk sampling commits while main accounting writer lock is held", s => {
    using var c=new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder{DataSource=s.DatabasePath}.ToString());c.Open();
    using var transaction=c.BeginTransaction();var sample=Task.Run(()=>s.RecordDiskSample(new(start,"v",1000,900)));
    var independent=sample.Wait(TimeSpan.FromSeconds(10));transaction.Rollback();sample.GetAwaiter().GetResult();Eq(true,independent);
}));
tests.Add(("transient auto coverage does not contaminate future windows and expires", s => {
    Baseline(s);s.ApplyBatch([F("A",null,@"C:\old\a")],[],start.AddDays(1));s.CompleteScan("partial",start.AddDays(1),false,false);
    s.ApplyBatch([F("A",100,@"C:\old\a")],[],start.AddDays(2));
    Eq(false,s.QueryReport(start.AddDays(3),start.AddDays(4)).CoverageNotes.Any(x=>x.Code is "allocation-unavailable" or "incomplete-scan"));
    s.Prune(start.AddDays(367));Eq(false,Report(s).CoverageNotes.Any(x=>x.Code is "allocation-unavailable" or "incomplete-scan"));
}));
tests.Add(("missing sample sidecar is visible in read-only reports without recreation",s=>{
    Baseline(s);Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();File.Delete(s.DiskSampleDatabasePath);
    var read=new MonitorStore(s.DatabasePath,true);read.Initialize();var report=Report(read);
    Eq((long?)null,report.DiskDeltaBytes);Eq(true,report.CoverageNotes.Any(x=>x.Code=="samples-unavailable"));Eq(false,File.Exists(s.DiskSampleDatabasePath));
}));
tests.Add(("legacy disk samples migrate once into independent sample database",s=>{
    var value=new DiskSample(start,"legacy",1000,900);
    using(var c=new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder{DataSource=s.DatabasePath}.ToString()))
    {c.Open();using var cmd=c.CreateCommand();cmd.CommandText="INSERT INTO samples VALUES($at,$volume,$payload)";cmd.Parameters.AddWithValue("$at",start.ToUnixTimeMilliseconds());cmd.Parameters.AddWithValue("$volume",value.VolumeId);cmd.Parameters.AddWithValue("$payload",System.Text.Json.JsonSerializer.Serialize(value));cmd.ExecuteNonQuery();}
    s.Initialize();s.Initialize();Eq(value,Report(s).Samples.Single());
}));
tests.Add(("ten thousand file migration and reconciliation allow continuous independent sampling",s=>{
    s.ApplyBatch([new("D",[@"C:\large"],0,0,true),F("outside",7,@"C:\large-sibling\keep")],[],start,baseline:true,scanId:"before");
    for(var i=0;i<10000;i+=250)s.ApplyBatch(Enumerable.Range(i,250).Select(n=>F("L"+n.ToString("D5"),4,@"C:\large\sub\"+n)).ToArray(),[],start,baseline:true,scanId:"before");
    var sequence=0;
    void WithSamples(Action operation,string label)
    {
        using var began=new ManualResetEventSlim();var timer=System.Diagnostics.Stopwatch.StartNew();
        var worker=Task.Run(()=>{began.Set();operation();});began.Wait();var progress=0;
        while(!worker.IsCompleted)
        {
            s.RecordDiskSample(new(start.AddDays(200).AddMilliseconds(sequence++),"v",100000,90000));
            if(!worker.IsCompleted)progress++;Thread.Sleep(10);
        }
        worker.GetAwaiter().GetResult();Console.WriteLine($"MEASURE {label}: {timer.ElapsedMilliseconds} ms, {progress} independent samples completed while operation was active");Eq(true,progress>0);
    }
    WithSamples(()=>s.ApplyBatch([new("D",[@"C:\renamed"],0,0,true)],[],start.AddHours(1)),"10k descendant migration");
    Eq(0L,Report(s).NetBytes);Eq(40000L,Report(s).Folders.Single(x=>x.Path==@"C:\renamed").MigratedInBytes);
    s.ApplyBatch([F("outside",7,@"C:\large-sibling\keep")],[],start.AddHours(2),scanId:"keep");
    WithSamples(()=>s.CompleteScan("keep",start.AddHours(3),true,false),"10k unseen reconciliation");
    Eq(40000L,Report(s).ReleasedBytes);s.ApplyBatch([], ["outside"],start.AddHours(4));Eq(40007L,Report(s).ReleasedBytes);
}));
tests.Add(("Unicode case variants migrate descendants across keyset pages and reopen",s=>{
    s.ApplyBatch([new("unicode-directory",[@"C:\Ä"],0,0,true),F("unicode-sibling",7,@"C:\ä-sibling\keep")],[],start,baseline:true);
    s.ApplyBatch(Enumerable.Range(0,300).Select(i=>F("unicode-"+i.ToString("D3"),4,@"C:\ä\sub\"+i)).ToArray(),[],start,baseline:true);
    // A reopened store must register the same path comparison as the original writer.
    var reopened=new MonitorStore(s.DatabasePath);reopened.Initialize();
    reopened.ApplyBatch([new("unicode-directory",[@"C:\Ö"],0,0,true)],[],start.AddHours(1));
    Eq(0L,Report(reopened).NetBytes);Eq(1200L,Report(reopened).Folders.SingleOrDefault(x=>x.Path==@"C:\Ö")?.MigratedInBytes??0);
    reopened.ApplyBatch([], ["unicode-000"],start.AddHours(2));Eq(@"C:\Ö\sub\0",Report(reopened).Files.Last().OldPath);
    reopened.ApplyBatch([], ["unicode-sibling"],start.AddHours(3));Eq(@"C:\ä-sibling\keep",Report(reopened).Files.Last().OldPath);
    var read=new MonitorStore(s.DatabasePath,true);read.Initialize();Eq(11L,Report(read).ReleasedBytes);
}));
foreach (var (name, test) in tests)
{
    var directory = Path.Combine(Path.GetTempPath(), "DriveLedgerTests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
    try { var s = new MonitorStore(Path.Combine(directory, "test.db")); s.Initialize(); test(s); Console.WriteLine($"PASS {name}"); }
    catch (Exception e) { failed++; Console.WriteLine($"FAIL {name}: {e.GetType().Name}: {e.Message}"); }
    finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(directory, true); }
}
Console.WriteLine($"{tests.Count - failed}/{tests.Count} passed"); return failed == 0 ? 0 : 1;
