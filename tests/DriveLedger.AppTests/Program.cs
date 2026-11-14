using DriveLedger.App;
using DriveLedger.Core;
using System.Security.Principal;

if (SingleInstanceTests.IsHost(args))
{
    try { return SingleInstanceTests.RunHost(args); }
    catch (Exception error) { Console.Error.WriteLine(error); return 3; }
}

var root = Path.GetFullPath(Path.Combine("artifacts", "app-tests", Guid.NewGuid().ToString("N")));
Directory.CreateDirectory(root);
int passed = 0, total = 0;
void Check(string name, Action test)
{
    total++;
    try { test(); passed++; Console.WriteLine("PASS " + name); }
    catch (Exception e) { Console.WriteLine("FAIL " + name + ": " + e.Message); }
}
void Assert(bool value) { if (!value) throw new Exception("Assertion failed"); }
void Reject(Action action) { try { action(); } catch (ArgumentException) { return; } throw new Exception("Expected argument rejection"); }
Check("cold viewer never creates directory or DB", () => {
    var path = Path.Combine(root, "absent");
    Assert(Viewer.Read(path, DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now) is null);
    Assert(!Directory.Exists(path));
});
Check("unavailable data does not fall back", () => {
    Assert(Viewer.Read(@"Z:\DriveLedger-unavailable", DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now) is null);
});
Check("arguments isolate fixture pipe and reject conflicting modes", () => {
    Reject(() => Options.Parse(["--collect"]));
    Reject(() => Options.Parse(["--install", "--service"]));
    Reject(() => Options.Parse(["--wat"]));
    Reject(() => Options.Parse(["--fixture-root", root]));
    Reject(() => Options.Parse(["--collect", "--fixture-root", root, "--pipe", "DriveLedger.Control"]));
    Reject(() => Options.Parse(["--collect", "--fixture-root", root, "--pipe", "DriveLedger.Test"]));
    Reject(() => Options.Parse(["--collect", "--fixture-root", root, "--pipe", "DriveLedger.Test", "--data-dir", root]));
    var scan = Path.Combine(root, "scan"); var data = Path.Combine(root, "data");
    var o = Options.Parse(["--collect", "--fixture-root", scan, "--pipe", "DriveLedger.Test." + Guid.NewGuid().ToString("N"), "--data-dir", data]);
    Assert(o.FixtureRoot == scan && o.DataDir == data);
});
Check("installer rejects roots overlap system identity and reparse parents", () => {
    var sid = WindowsIdentity.GetCurrent().User!.Value;
    Reject(() => InstallPlan.Create(root, @"C:\", Path.Combine(root, "data"), sid));
    Reject(() => InstallPlan.Create(root, root, Path.Combine(root, "data"), sid));
    Reject(() => InstallPlan.Create(root, Path.Combine(root, "app"), root, sid));
    Reject(() => InstallPlan.Create(root, Path.Combine(root, "app"), Path.Combine(root, "data"), "S-1-5-18"));
    var source = Path.Combine(root, "source"); Directory.CreateDirectory(source);
    var plan = InstallPlan.Create(source, Path.Combine(root, "product", "App"), Path.Combine(root, "product", "Data"), sid);
    Assert(plan.Sid == sid);
});
Check("export quotes paths formulas and preserves unknown delta", () => {
    var db = new MonitorStore(Path.Combine(root, "export.db")); db.Initialize();
    var now = DateTimeOffset.Now;
    db.ApplyBatch([new("f", ["C:\\=SUM,\"x.txt"], 4096, 20)], [], now);
    var report = db.QueryReport(now.AddMinutes(-1), now.AddMinutes(1));
    var csv = Viewer.Export(report);
    Assert(csv.Contains("未知") && csv.Contains("4096") && csv.Contains("\"\"x.txt"));
    Assert(Accounting.CsvRow("=1+1").Contains("'=1+1"));
});
Check("unavailable collection path faults without creating a fallback", () => {
    var scan = Path.Combine(root, "scan"); Directory.CreateDirectory(scan);
    var o = Options.Parse(["--collect", "--fixture-root", scan, "--pipe", "DriveLedger.Test." + Guid.NewGuid().ToString("N"), "--data-dir", @"Z:\DriveLedger-unavailable"]);
    try { CollectionHost.RunAsync(o, CancellationToken.None).GetAwaiter().GetResult(); }
    catch (IOException) { return; }
    throw new Exception("Unavailable data must fail");
});
Check("installer rejects actual junction ancestor", () => {
    var target = Path.Combine(root, "junction-target"); Directory.CreateDirectory(target);
    var link = Path.Combine(root, "junction");
    var start = new System.Diagnostics.ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
    start.ArgumentList.Add("/c"); start.ArgumentList.Add("mklink"); start.ArgumentList.Add("/J"); start.ArgumentList.Add(link); start.ArgumentList.Add(target);
    using var process = System.Diagnostics.Process.Start(start)!; process.WaitForExit();
    if (process.ExitCode != 0) throw new Exception("Junction fixture setup failed");
    try { Reject(() => InstallPlan.Create(Path.Combine(root, "source"), Path.Combine(link, "app"), Path.Combine(root, "data"), WindowsIdentity.GetCurrent().User!.Value)); }
    finally { Directory.Delete(link, false); }
});
Check("installer never takes ownership of system or unrelated populated data", () => {
    var source = Path.Combine(root, "source"); var sid = WindowsIdentity.GetCurrent().User!.Value;
    Reject(() => InstallPlan.Create(source, Path.Combine(root, "app"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32"), sid));
    var unrelated = Path.Combine(root, "unrelated"); Directory.CreateDirectory(unrelated); File.WriteAllText(Path.Combine(unrelated, "personal.txt"), "preserve");
    Reject(() => InstallPlan.Create(source, Path.Combine(root, "app"), unrelated, sid));
    Assert(File.ReadAllText(Path.Combine(unrelated, "personal.txt")) == "preserve");
});
FixRoundTests.Run(root, Check, Assert);
UpgradeLifetimeTests.Run(root, Check, Assert);
SingleInstanceTests.Run(root, Check, Assert);
Console.WriteLine($"{passed}/{total} passed");
return passed == total ? 0 : 1;
