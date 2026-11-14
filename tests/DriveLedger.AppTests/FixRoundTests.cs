using System.Security.AccessControl;
using System.Security.Principal;
using System.Diagnostics;
using System.Reflection;
using System.Windows.Controls;
using System.Windows.Data;
using System.ComponentModel;
using DriveLedger.App;

static class FixRoundTests
{
    public static void Run(string root, Action<string, Action> check, Action<bool> assert)
    {
        string sid = WindowsIdentity.GetCurrent().User!.Value;
        void MustReject(Action action)
        {
            try { action(); } catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException) { return; }
            throw new Exception("Unsafe input accepted");
        }
        string NewRoot() { var p = Path.Combine(root, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(p); return p; }
        check("mixed case production pipe rejected in GUI collect and service fixtures", () => {
            foreach (var mode in new[] { "", "--collect", "--service" })
            {
                var args = new List<string>(); if (mode.Length > 0) args.Add(mode);
                args.AddRange(["--fixture-root", Path.Combine(root, "scan"), "--data-dir", Path.Combine(root, "data"), "--pipe", "dRiVeLeDgEr.CoNtRoL", "--owner-sid", sid]);
                MustReject(() => Options.Parse(args.ToArray()));
            }
        });
        check("forged markers and unrelated product-container siblings never authorize takeover", () => {
            var r = NewRoot(); var source = Path.Combine(r, "source"); Directory.CreateDirectory(source);
            var container = Path.Combine(r, "product"); var app = Path.Combine(container, "App"); var data = Path.Combine(container, "Data");
            Directory.CreateDirectory(app); Directory.CreateDirectory(data);
            File.WriteAllText(Path.Combine(data, ".driveledger-data-owner"), "forged"); File.WriteAllText(Path.Combine(app, "installed-files.json"), "{}");
            File.WriteAllText(Path.Combine(container, "personal.txt"), "outside");
            var acl = new DirectoryInfo(container).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.All);
            MustReject(() => InstallPlan.Create(source, app, data, sid));
            assert(File.ReadAllText(Path.Combine(container, "personal.txt")) == "outside");
            assert(acl == new DirectoryInfo(container).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.All));
        });
        foreach (var hard in new[] { false, true })
            check((hard ? "hardlink" : "symlink") + " destinations preserve external content and ACL before any mutation", () => {
                var r = NewRoot(); var source = Path.Combine(r, "source"); Directory.CreateDirectory(source);
                var container = Path.Combine(r, "product"); var app = Path.Combine(container, "App"); var data = Path.Combine(container, "Data");
                var outside = Path.Combine(r, "external.txt"); File.WriteAllText(outside, "must survive");
                var acl = new FileInfo(outside).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.All);
                using (var setup = InstallSession.Open(new(source, app, data, sid))) setup.CommitReceipt([]);
                foreach (var target in new[] { Path.Combine(app, "DriveLedger.App.exe"), Path.Combine(app, "installed-files.json"), Path.Combine(data, ".driveledger-data-owner") })
                { if (File.Exists(target)) File.Delete(target); Link(target, outside, hard); }
                MustReject(() => { using var session = InstallSession.Open(new(source, app, data, sid)); });
                assert(File.ReadAllText(outside) == "must survive");
                assert(acl == new FileInfo(outside).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.All));
            });
        check("fresh secure session and validated upgrade atomically replace payload", () => {
            var r = NewRoot(); var source = Path.Combine(r, "source"); Directory.CreateDirectory(source);
            var file = Path.Combine(source, "DriveLedger.App.exe"); File.WriteAllText(file, "v1");
            var plan = new InstallPlan(source, Path.Combine(r, "product", "App"), Path.Combine(r, "product", "Data"), sid);
            using (var session = InstallSession.Open(plan)) { session.ReplacePayload(file, "DriveLedger.App.exe"); session.CommitReceipt(["DriveLedger.App.exe"]); }
            File.WriteAllText(file, "v2");
            using (var session = InstallSession.Open(plan)) { session.ReplacePayload(file, "DriveLedger.App.exe"); session.CommitReceipt(["DriveLedger.App.exe"]); }
            assert(File.ReadAllText(Path.Combine(plan.AppDir, "DriveLedger.App.exe")) == "v2");
        });
        check("pinned directories resist replacement and post-validation hardlinks retain old bytes and ACL", () => {
            var r = NewRoot(); var source = Path.Combine(r, "source"); Directory.CreateDirectory(source);
            var file = Path.Combine(source, "DriveLedger.App.exe"); File.WriteAllText(file, "v1");
            var plan = new InstallPlan(source, Path.Combine(r, "product", "App"), Path.Combine(r, "product", "Data"), sid);
            using (var setup = InstallSession.Open(plan)) { setup.ReplacePayload(file, "DriveLedger.App.exe"); setup.CommitReceipt(["DriveLedger.App.exe"]); }
            using var session = InstallSession.Open(plan);
            MustReject(() => Directory.Move(plan.DataDir, plan.DataDir + "-replaced"));
            var external = Path.Combine(r, "later-hardlink.exe");
            Link(external, Path.Combine(plan.AppDir, "DriveLedger.App.exe"), true);
            var acl = new FileInfo(external).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.All);
            File.WriteAllText(file, "v2"); session.ReplacePayload(file, "DriveLedger.App.exe");
            assert(File.ReadAllText(external) == "v1" && acl == new FileInfo(external).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.All));
            var externalReceipt = Path.Combine(r, "later-receipt.json");
            Link(externalReceipt, Path.Combine(plan.AppDir, "installed-files.json"), true);
            var oldReceipt = File.ReadAllText(externalReceipt);
            var receiptAcl = new FileInfo(externalReceipt).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.All);
            session.CommitReceipt(["DriveLedger.App.exe", "extra.dll"]);
            assert(File.ReadAllText(externalReceipt) == oldReceipt && receiptAcl == new FileInfo(externalReceipt).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.All));
        });
        check("desktop shortcut publication replaces raced link entry without touching its target", () => {
            var r = NewRoot(); var source = Path.Combine(r, "source"); Directory.CreateDirectory(source);
            var desktop = Path.Combine(r, "desktop"); Directory.CreateDirectory(desktop);
            var destination = Path.Combine(desktop, "C盘空间账本.lnk");
            var outside = Path.Combine(r, "outside-shortcut.txt"); File.WriteAllText(outside, "preserve");
            var acl = new FileInfo(outside).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.All);
            var plan = new InstallPlan(source, Path.Combine(r, "product", "App"), Path.Combine(r, "product", "Data"), sid);
            using var session = InstallSession.Open(plan, destination);
            Link(destination, outside, false); // Simulate an ordinary-user replacement after preflight.
            session.PublishShortcut(path => File.WriteAllText(path, "new shortcut"));
            assert(File.ReadAllText(destination) == "new shortcut" && File.ReadAllText(outside) == "preserve");
            assert(acl == new FileInfo(outside).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.All));
        });
        check("protected receipt product path identity and ACL must all match for upgrade", () => {
            foreach (var corrupt in new[] { "product", "path", "account", "acl", "owner", "nullacl", "sibling" })
            {
                var r = NewRoot(); var source = Path.Combine(r, "source"); Directory.CreateDirectory(source);
                var plan = new InstallPlan(source, Path.Combine(r, "product", "App"), Path.Combine(r, "product", "Data"), sid);
                using (var setup = InstallSession.Open(plan)) setup.CommitReceipt([]);
                var metadata = Path.Combine(Path.GetDirectoryName(plan.AppDir)!, InstallSession.ContainerReceipt);
                var text = File.ReadAllText(metadata);
                switch (corrupt)
                {
                    case "product": File.WriteAllText(metadata, text.Replace("CDriveSpaceMonitor", "DifferentProduct")); break;
                    case "path":
                        var wrongPath = System.Text.Json.JsonSerializer.Deserialize<InstallSession.Receipt>(text)! with { AppDir = plan.AppDir + "-other" };
                        File.WriteAllText(metadata, System.Text.Json.JsonSerializer.Serialize(wrongPath)); break;
                    case "account": File.WriteAllText(metadata, text.Replace(sid, "S-1-5-21-1-2-3-1000")); break;
                    case "acl":
                        var acl = new FileInfo(metadata).GetAccessControl(); acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid), FileSystemRights.Write, AccessControlType.Allow)); new FileInfo(metadata).SetAccessControl(acl); break;
                    case "owner":
                        var ownerAcl = new FileInfo(metadata).GetAccessControl(); ownerAcl.SetOwner(new SecurityIdentifier(sid)); new FileInfo(metadata).SetAccessControl(ownerAcl); break;
                    case "nullacl":
                        var raw = new RawSecurityDescriptor(ControlFlags.DiscretionaryAclPresent, new SecurityIdentifier("S-1-5-32-544"), new SecurityIdentifier("S-1-5-32-544"), null, null);
                        var bytes = new byte[raw.BinaryLength]; raw.GetBinaryForm(bytes, 0);
                        var nullAcl = new FileSecurity(); nullAcl.SetSecurityDescriptorBinaryForm(bytes); new FileInfo(metadata).SetAccessControl(nullAcl); break;
                    case "sibling": File.WriteAllText(Path.Combine(Path.GetDirectoryName(plan.AppDir)!, "personal.txt"), "preserve"); break;
                }
                var dataAcl = new DirectoryInfo(plan.DataDir).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.All);
                var before = File.ReadAllText(metadata);
                MustReject(() => { using var upgrade = InstallSession.Open(plan); });
                assert(File.ReadAllText(metadata) == before);
                assert(new DirectoryInfo(plan.DataDir).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.All) == dataAcl);
            }
        });
        check("offline read-only viewer succeeds after all writers and pools close with no user writes", () => {
            var r = NewRoot(); var now = DateTimeOffset.Now;
            var store = new DriveLedger.Core.MonitorStore(Path.Combine(r, "ledger.db")); store.Initialize();
            store.ApplyBatch([new("f", ["C:\\fixture.bin"], 4096, 4096)], [], now);
            store.RecordDiskSample(new(now.AddSeconds(-1), "v", 100000, 90000));
            store.RecordDiskSample(new(now.AddSeconds(1), "v", 100000, 85000));
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            foreach (var db in new[] { store.DatabasePath, store.DiskSampleDatabasePath })
                assert(File.Exists(db + "-wal") && File.Exists(db + "-shm"));
            var dir = new DirectoryInfo(r); var original = dir.GetAccessControl(); var restricted = dir.GetAccessControl();
            var deny = new FileSystemAccessRule(new SecurityIdentifier(sid), FileSystemRights.Write | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Deny);
            restricted.AddAccessRule(deny); dir.SetAccessControl(restricted);
            try
            {
                bool denied = false; try { File.WriteAllText(Path.Combine(r, "forbidden"), "x"); } catch (UnauthorizedAccessException) { denied = true; }
                assert(denied);
                var read = Viewer.Read(r, now.AddMinutes(-1), now.AddMinutes(1))!;
                assert(read.NetBytes == 4096 && read.DiskDeltaBytes == 5000 && read.Samples.Count == 2);
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                assert(!File.Exists(Path.Combine(r, "forbidden")));
            }
            finally { dir.SetAccessControl(original); }
        });
        check("actual WPF size-column sort orders KiB MiB GiB numerically", () => {
            Exception? failure = null;
            var thread = new Thread(() => {
                DriveLedger.App.MainWindow? window = null;
                try {
                    window = new(Options.Parse([]));
                    var type = typeof(DriveLedger.App.MainWindow).GetNestedType("FolderRow", BindingFlags.NonPublic)!;
                    var rows = new[] { ("small", 900L * 1024), ("medium", 12L * 1024 * 1024), ("large", 2L * 1024 * 1024 * 1024) }.Select(x => Activator.CreateInstance(type, x.Item1, x.Item2, x.Item2, x.Item2, x.Item2, x.Item2)!).ToArray();
                    var grid = (DataGrid)window.FindName("Folders"); grid.ItemsSource = rows;
                    var view = CollectionViewSource.GetDefaultView(grid.ItemsSource);
                    foreach (var column in grid.Columns.Skip(1))
                    {
                        view.SortDescriptions.Clear();
                        view.SortDescriptions.Add(new SortDescription(column.SortMemberPath, ListSortDirection.Ascending));
                        assert(view.Cast<object>().Select(x => (string)type.GetProperty("Path")!.GetValue(x)!).SequenceEqual(new[] { "small", "medium", "large" }));
                        view.SortDescriptions.Clear();
                        view.SortDescriptions.Add(new SortDescription(column.SortMemberPath, ListSortDirection.Descending));
                        assert(view.Cast<object>().Select(x => (string)type.GetProperty("Path")!.GetValue(x)!).SequenceEqual(new[] { "large", "medium", "small" }));
                    }
                    var fileType = typeof(DriveLedger.App.MainWindow).GetNestedType("FileRow", BindingFlags.NonPublic)!;
                    var fileRows = new[] { ("small", 900L * 1024), ("medium", 12L * 1024 * 1024), ("large", 2L * 1024 * 1024 * 1024) }.Select(x =>
                    {
                        var source = new DriveLedger.Core.FileChange(x.Item1, null, x.Item1, DateTimeOffset.Now, x.Item2, x.Item2, 0, x.Item2, x.Item2, "observed");
                        return Activator.CreateInstance(fileType, source, x.Item1, "已有文件变化", DriveLedger.Core.Accounting.DisplayBytes(x.Item2), DriveLedger.Core.Accounting.DisplayBytes(x.Item2), "", "")!;
                    }).ToArray();
                    var files = (DataGrid)window.FindName("Files"); files.ItemsSource = fileRows;
                    var fileView = CollectionViewSource.GetDefaultView(files.ItemsSource);
                    foreach (var column in files.Columns.Where(c => (string)c.Header is "增加" or "释放"))
                    {
                        fileView.SortDescriptions.Clear();
                        fileView.SortDescriptions.Add(new SortDescription(column.SortMemberPath, ListSortDirection.Ascending));
                        assert(fileView.Cast<object>().Select(x => (string)fileType.GetProperty("Path")!.GetValue(x)!).SequenceEqual(new[] { "small", "medium", "large" }));
                    }
                } catch (Exception e) { failure = e; }
                finally { if (window is not null) { typeof(DriveLedger.App.MainWindow).GetField("exiting", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(window, true); window.Close(); } }
            });
            thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
            if (failure is not null) throw failure;
        });
    }
    public static void Link(string target, string existing, bool hard)
    {
        var start = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        foreach (var a in new[] { "/c", "mklink", hard ? "/H" : "", target, existing }.Where(a => a.Length > 0)) start.ArgumentList.Add(a);
        using var p = Process.Start(start)!; p.WaitForExit(); if (p.ExitCode != 0) throw new Exception("Link setup failed");
    }
}
