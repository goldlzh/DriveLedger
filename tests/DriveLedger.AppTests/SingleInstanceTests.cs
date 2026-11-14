using DriveLedger.App;
using System.Diagnostics;
using System.IO.Pipes;

internal static class SingleInstanceTests
{
    public static bool IsHost(string[] args) => args.Length > 0 && args[0] == "--instance-test-host";
    public static int RunHost(string[] args)
    {
        using var instance = ViewerInstance.Acquire(args[1]);
        if (!instance.IsPrimary)
        {
            if (!instance.ActivateExisting()) return 2;
            Console.WriteLine("SECONDARY");
            return 0;
        }
        Console.WriteLine($"PRIMARY {Environment.ProcessId} {instance.PipeName}");
        Console.Out.Flush();
        if (args.Length > 2) Thread.Sleep(int.Parse(args[2]));
        instance.StartListening(() => { Console.WriteLine("ACTIVATED"); Console.Out.Flush(); });
        Console.ReadLine();
        return 0;
    }

    public static void Run(string root, Action<string, Action> check, Action<bool> assert)
    {
        string Key() => Guid.NewGuid().ToString("N");
        check("second viewer process activates the owner and exits", () => {
            var key = Key(); using var owner = new Host(key); assert(owner.Read().StartsWith("PRIMARY "));
            using var second = new Host(key); assert(second.Read() == "SECONDARY"); second.WaitSuccess();
            assert(owner.Read() == "ACTIVATED" && !owner.Process.HasExited);
        });
        check("simultaneous viewer launches elect exactly one primary", () => {
            var key = Key(); var hosts = Enumerable.Range(0, 6).Select(_ => new Host(key)).ToArray();
            try
            {
                var starts = hosts.Select(h => h.Read()).ToArray();
                assert(starts.Count(s => s.StartsWith("PRIMARY ")) == 1);
                var primary = Array.FindIndex(starts, s => s.StartsWith("PRIMARY "));
                for (var i = 0; i < hosts.Length; i++)
                    if (i != primary) { assert(starts[i] == "SECONDARY"); hosts[i].WaitSuccess(); }
                for (var i = 1; i < hosts.Length; i++) assert(hosts[primary].Read() == "ACTIVATED");
                assert(!hosts[primary].Process.HasExited);
            }
            finally { foreach (var host in hosts) host.Dispose(); }
        });
        check("launch during primary startup waits for activation listener", () => {
            var key = Key(); using var owner = new Host(key, 600); assert(owner.Read().StartsWith("PRIMARY "));
            using var second = new Host(key); assert(second.Read() == "SECONDARY"); second.WaitSuccess();
            assert(owner.Read() == "ACTIVATED");
        });
        check("exiting viewer releases ownership for a later launch", () => {
            var key = Key(); using (var owner = new Host(key)) { assert(owner.Read().StartsWith("PRIMARY ")); owner.Quit(); }
            using var replacement = new Host(key); assert(replacement.Read().StartsWith("PRIMARY "));
        });
        check("terminated viewer leaves no stale instance lock", () => {
            var key = Key(); using (var owner = new Host(key))
            { assert(owner.Read().StartsWith("PRIMARY ")); owner.Process.Kill(); owner.Process.WaitForExit(); }
            using var replacement = new Host(key); assert(replacement.Read().StartsWith("PRIMARY "));
        });
        check("independent fixture scopes can own viewers independently", () => {
            using var first = new Host(Key()); using var second = new Host(Key());
            assert(first.Read().StartsWith("PRIMARY ") && second.Read().StartsWith("PRIMARY "));
        });
        check("installed and unpacked production configurations share one scope", () => {
            var normal = Options.Parse([]);
            var alternate = Options.Parse(["--data-dir", Path.Combine(root, "other-data")]);
            assert(ViewerInstance.ScopeFor(normal) == ViewerInstance.ScopeFor(alternate));
            var scan = Path.Combine(root, "instance-scan");
            var fixture = Options.Parse(["--fixture-root", scan, "--data-dir", Path.Combine(root, "instance-data"), "--pipe", "DriveLedger.Fixture.instance"]);
            assert(ViewerInstance.ScopeFor(normal) != ViewerInstance.ScopeFor(fixture));
        });
        check("idle activation client cannot block subsequent launches indefinitely", () => {
            var key = Key(); using var owner = new Host(key);
            var start = owner.Read().Split(' '); assert(start[0] == "PRIMARY");
            using var idle = new NamedPipeClientStream(".", start[2], PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            idle.Connect(3000);
            using var second = new Host(key); assert(second.Read() == "SECONDARY"); second.WaitSuccess();
            assert(owner.Read() == "ACTIVATED");
        });
    }

    sealed class Host : IDisposable
    {
        public Process Process { get; }
        public Host(string key, int delay = 0)
        {
            var start = new ProcessStartInfo(Environment.ProcessPath!)
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
            if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase))
                start.ArgumentList.Add(typeof(SingleInstanceTests).Assembly.Location);
            start.ArgumentList.Add("--instance-test-host"); start.ArgumentList.Add(key);
            if (delay > 0) start.ArgumentList.Add(delay.ToString());
            Process = Process.Start(start)!;
        }
        public string Read() => Process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(12)).GetAwaiter().GetResult()
            ?? throw new Exception("Instance host ended: " + Process.StandardError.ReadToEnd());
        public void WaitSuccess()
        {
            if (!Process.WaitForExit(10000)) throw new Exception("Instance host did not exit");
            if (Process.ExitCode != 0) throw new Exception("Instance host failed: " + Process.StandardError.ReadToEnd());
        }
        public void Quit() { if (!Process.HasExited) Process.StandardInput.WriteLine("quit"); WaitSuccess(); }
        public void Dispose()
        {
            try { Quit(); }
            catch { if (!Process.HasExited) { Process.Kill(); Process.WaitForExit(); } }
            finally { Process.Dispose(); }
        }
    }
}
