using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using DriveLedger.Core;
using DriveLedger.Windows;

var tests = new List<(string, Action)>();
void Check(bool value)
{
    if (!value)
        throw new Exception("assertion failed");
}

void Throws<T>(Action action)
    where T : Exception
{
    try
    {
        action();
    }
    catch (T)
    {
        return;
    }

    throw new Exception("expected " + typeof(T).Name);
}

byte[] Record()
{
    var b = new byte[64];
    BinaryPrimitives.WriteInt32LittleEndian(b, 64);
    b[4] = 2;
    b[8] = 42;
    b[16] = 7;
    b[24] = 100;
    BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(40), 0x100);
    b[56] = 2;
    b[58] = 60;
    Encoding.Unicode.GetBytes("a").CopyTo(b, 60);
    return b;
}

tests.Add(("USN valid V2 decodes full FRN reason and filename", () =>
{
    var r = UsnParser.Parse(Record()).Single();
    Check(r.FileReference == 42 && r.ParentReference == 7 && r.Usn == 100 && r.Reason == 0x100 && r.Name == "a");
}));
tests.Add(("USN truncated records fail closed", () =>
{
    Throws<InvalidDataException>(() => UsnParser.Parse(Record().AsSpan(0, 63)));
    var b = Record();
    b[58] = 63;
    Throws<InvalidDataException>(() => UsnParser.Parse(b));
}));
tests.Add(("USN unsupported versions fail visibly", () =>
{
    var b = Record();
    b[4] = 4;
    Throws<NotSupportedException>(() => UsnParser.Parse(b));
}));
void Fixture(Action<string> test)
{
    var p = Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "native-fixtures", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(p);
    try
    {
        test(p);
    }
    finally
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(p, true);
    }
}

tests.Add(("real metadata observes create grow truncate and hardlink dedup", () => Fixture(p =>
{
    var f = Path.Combine(p, "a");
    File.WriteAllBytes(f, new byte[4096]);
    var a = NativeMetadata.Read(f);
    Check(a.LogicalBytes == 4096 && a.AllocatedBytes > 0);
    File.WriteAllBytes(f, new byte[131072]);
    var b = NativeMetadata.Read(f);
    Check(b.FileId == a.FileId && b.AllocatedBytes > a.AllocatedBytes);
    using (var s = File.OpenWrite(f))
        s.SetLength(0);
    Check(NativeMetadata.Read(f).AllocatedBytes == 0);
    File.WriteAllBytes(f, new byte[4096]);
    Check(NativeFixture.CreateHardLink(Path.Combine(p, "b"), f, IntPtr.Zero));
    var h = NativeMetadata.Read(f);
    Check(h.Paths.Count == 2 && NativeMetadata.Read(Path.Combine(p, "b")).FileId == h.FileId);
    var store = new MonitorStore(Path.Combine(p, "ledger.db"));
    store.Initialize();
    store.ApplyBatch([h, NativeMetadata.Read(Path.Combine(p, "b"))], [], DateTimeOffset.Now);
    Check(store.QueryReport(DateTimeOffset.Now.AddMinutes(-1), DateTimeOffset.Now).NetBytes == h.AllocatedBytes);
})));
tests.Add(("walk records junction without traversing target", () => Fixture(p =>
{
    var target = Path.Combine(p, "outside");
    Directory.CreateDirectory(target);
    File.WriteAllText(Path.Combine(target, "secret"), "fixture");
    var root = Path.Combine(p, "root");
    Directory.CreateDirectory(root);
    NativeFixture.Junction(Path.Combine(root, "link"), target);
    var items = NativeMetadata.Walk(root, e => throw e, CancellationToken.None).ToArray();
    Check(items.Any(x => x.IsReparsePoint));
    Check(!items.Any(x => x.Paths.Any(y => y.EndsWith("secret"))));
    Check(NativeFixture.RemoveDirectory(Path.Combine(root, "link")));
})));
tests.Add(("actual sparse and compressed allocation differs from logical size", () => Fixture(p =>
{
    var sparse = Path.Combine(p, "sparse");
    using (var f = new FileStream(sparse, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete))
    {
        NativeFixture.Control(f.SafeFileHandle, 0x900c4, []);
        f.SetLength(8 * 1024 * 1024);
        f.Position = 4 * 1024 * 1024;
        f.Write(new byte[4096]);
        f.Flush(true);
    }

    var a = NativeMetadata.Read(sparse);
    Console.WriteLine($"SPARSE observed {a.AllocatedBytes}/{a.LogicalBytes}");
    Check(a.LogicalBytes == 8 * 1024 * 1024 && a.AllocatedBytes < 8 * 1024 * 1024);
    var compressed = Path.Combine(p, "compressed");
    using (var f = new FileStream(compressed, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete))
    {
        NativeFixture.Control(f.SafeFileHandle, 0x9c040, [2, 0]);
        f.Write(new byte[1024 * 1024]);
        f.Flush(true);
    }

    var b = NativeMetadata.Read(compressed);
    Check(b.LogicalBytes == 1024 * 1024 && b.AllocatedBytes < 1024 * 1024);
    Console.WriteLine($"MEASURE sparse {a.AllocatedBytes}/{a.LogicalBytes}; compressed {b.AllocatedBytes}/{b.LogicalBytes}");
})));
CollectorTests.Register(tests);
FixRoundTests.Register(tests);
tests.Add(("directory replaced after yield never traverses replacement junction", () => Fixture(p =>
{
    var root = Path.Combine(p, "root");
    var moved = Path.Combine(p, "moved");
    var target = Path.Combine(p, "target");
    Directory.CreateDirectory(root);
    Directory.CreateDirectory(target);
    File.WriteAllText(Path.Combine(root, "original"), "owned");
    File.WriteAllText(Path.Combine(target, "target-secret"), "owned");
    using var walk = NativeMetadata.Walk(root, e => throw e, CancellationToken.None).GetEnumerator();
    Check(walk.MoveNext() && walk.Current.IsDirectory);
    Directory.Move(root, moved);
    NativeFixture.Junction(root, target);
    try
    {
        while (walk.MoveNext())
            Check(!walk.Current.Paths.Any(x => x.EndsWith("target-secret")));
    }
    finally
    {
        Check(NativeFixture.RemoveDirectory(root));
    }
})));
var failed = 0;
foreach (var(name, test)in tests)
{
    try
    {
        test();
        Console.WriteLine("PASS " + name);
    }
    catch (Exception e)
    {
        failed++;
        Console.WriteLine($"FAIL {name}: {e}");
    }
}

Console.WriteLine($"{tests.Count - failed}/{tests.Count} native passed");
return failed == 0 ? 0 : 1;
static class NativeFixture
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "RemoveDirectoryW")]
    public static extern bool RemoveDirectory(string path);
    public static void Junction(string link, string target)
    {
        var start = new System.Diagnostics.ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in new[]
        {
            "/d",
            "/c",
            "mklink",
            "/J",
            link,
            target
        }

        )
            start.ArgumentList.Add(argument);
        using var process = System.Diagnostics.Process.Start(start)!;
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new IOException(process.StandardError.ReadToEnd());
    }

    public static void Control(Microsoft.Win32.SafeHandles.SafeFileHandle h, uint code, byte[] data)
    {
        if (!DeviceIoControl(h, code, data, data.Length, IntPtr.Zero, 0, out _, IntPtr.Zero))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool DeviceIoControl(Microsoft.Win32.SafeHandles.SafeFileHandle h, uint code, byte[] input, int inputLength, IntPtr output, int outputLength, out int returned, IntPtr overlapped);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateHardLinkW")]
    public static extern bool CreateHardLink(string name, string existing, IntPtr security);
}
