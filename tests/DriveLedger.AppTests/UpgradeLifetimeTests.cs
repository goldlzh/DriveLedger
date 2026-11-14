using DriveLedger.App;
using System.Security.Principal;
using System.Security.AccessControl;

static class UpgradeLifetimeTests
{
    public static void Run(string root, Action<string, Action> check, Action<bool> assert)
    {
        (InstallPlan Plan, string Source, string Active) Setup()
        {
            var r = Path.Combine(root, "upgrade-" + Guid.NewGuid().ToString("N"));
            var source = Path.Combine(r, "source"); Directory.CreateDirectory(source);
            var payload = Path.Combine(source, "DriveLedger.App.exe"); File.WriteAllText(payload, "v1");
            var plan = new InstallPlan(source, Path.Combine(r, "product", "App"), Path.Combine(r, "product", "Data"), WindowsIdentity.GetCurrent().User!.Value);
            using (var session = InstallSession.Open(plan)) { session.ReplacePayload(payload, "DriveLedger.App.exe"); session.CommitReceipt(["DriveLedger.App.exe"]); }
            var active = Path.Combine(plan.DataDir, "ledger.db.active.json");
            Persist(active, "initial"); File.WriteAllText(payload, "v2");
            return (plan, payload, active);
        }
        check("upgrade pre-stop validation allows live collector atomic active-state persistence", () =>
        {
            var fixture = Setup();
            using (var writer = new FileStream(fixture.Active + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None))
            {
                writer.Write(System.Text.Encoding.UTF8.GetBytes("exclusive writer in progress"));
                InstallSession.ValidateExisting(fixture.Plan);
                using var observing = InstallSession.Open(fixture.Plan);
                writer.Flush(true);
            }
            using var session = InstallSession.Open(fixture.Plan);
            Persist(fixture.Active, "before-stop");
            assert(ReadState(fixture.Active) == "before-stop");
        });
        foreach (bool hard in new[] { false, true })
            check("nonblocking Data observation still rejects " + (hard ? "hardlinks" : "symlinks") + " without changing targets", () =>
            {
                var fixture = Setup();
                var external = Path.Combine(fixture.Plan.Source, "outside.txt"); File.WriteAllText(external, "preserve");
                var acl = new FileInfo(external).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.All);
                File.Delete(fixture.Active); FixRoundTests.Link(fixture.Active, external, hard);
                bool rejected = false;
                try { using var session = InstallSession.Open(fixture.Plan); }
                catch (ArgumentException) { rejected = true; }
                assert(rejected && File.ReadAllText(external) == "preserve");
                assert(acl == new FileInfo(external).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.All));
            });
        foreach (var phase in new[] { "restart", "notification" })
            check("upgraded collector atomically persists active state during " + phase, () =>
            {
                var fixture = Setup();
                InstallationLifecycle.Apply(fixture.Plan, [fixture.Source], () => { }, _ => { },
                    start: () => { if (phase == "restart") { ExclusivePayloadRead(fixture.Plan); Persist(fixture.Active, phase); } },
                    notify: () => { if (phase == "notification") { ExclusivePayloadRead(fixture.Plan); Persist(fixture.Active, phase); } });
                assert(ReadState(fixture.Active) == phase);
                assert(File.ReadAllText(Path.Combine(fixture.Plan.AppDir, "DriveLedger.App.exe")) == "v2");
            });
    }
    static void Persist(string target, string state)
    {
        // Same physical tmp-write + replace operation used by Collector.PersistActiveState.
        File.WriteAllText(target + ".tmp", System.Text.Json.JsonSerializer.Serialize(new { State = state }));
        File.Move(target + ".tmp", target, overwrite: true);
    }
    static string ReadState(string path)
    {
        using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.GetProperty("State").GetString()!;
    }
    static void ExclusivePayloadRead(InstallPlan plan)
    {
        using var stream = new FileStream(Path.Combine(plan.AppDir, "DriveLedger.App.exe"), FileMode.Open, FileAccess.Read, FileShare.None);
        if (stream.ReadByte() != 'v') throw new Exception("Upgraded payload unreadable");
    }
}
