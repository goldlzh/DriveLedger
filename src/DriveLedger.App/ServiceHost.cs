using System;
using System.IO;
using System.Security.Principal;
using System.ServiceProcess;
using System.Threading;
using System.Threading.Tasks;
using DriveLedger.Core;
using DriveLedger.Windows;
namespace DriveLedger.App;

public static class CollectionHost
{
    public static async Task RunAsync(Options options, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(Path.GetPathRoot(options.DataDir))) throw new IOException("数据盘不可用；未改用 C 盘。");
        Directory.CreateDirectory(options.DataDir);
        var store = new MonitorStore(Path.Combine(options.DataDir, "ledger.db"));
        store.Initialize();
        var sid = options.Sid ?? WindowsIdentity.GetCurrent().User!.Value;
        var collector = new Collector(store, new CollectorOptions(SampleInterval: TimeSpan.FromSeconds(options.SampleSeconds), JournalInterval: TimeSpan.FromSeconds(options.PollSeconds), FixtureScanRoot: options.FixtureRoot, InstallAccountSid: sid));
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var collecting = collector.RunAsync(lifetime.Token);
        var pipe = new ControlPipeServer(collector, store, sid, options.Pipe).RunAsync(lifetime.Token);
        try
        {
            var first = await Task.WhenAny(collecting, pipe);
            if (!lifetime.IsCancellationRequested)
            {
                await first;
                throw new IOException("采集或通信任务意外结束");
            }
        }
        finally
        {
            lifetime.Cancel();
            try { await Task.WhenAll(collecting, pipe); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        }
    }
}

public sealed class ServiceHost : ServiceBase
{
    readonly Options options;
    CancellationTokenSource? lifetime;
    Task? work;
    public ServiceHost(Options options)
    {
        this.options = options;
        ServiceName = "CDriveSpaceMonitor";
        CanStop = true;
        AutoLog = true;
    }
    protected override void OnStart(string[] args)
    {
        lifetime = new CancellationTokenSource();
        work = Task.Run(() => CollectionHost.RunAsync(options, lifetime.Token));
        _ = work.ContinueWith(t =>
        {
            if (t.IsFaulted)
            {
                ExitCode = 1;
                Stop();
            }
        }, TaskScheduler.Default);
    }
    protected override void OnStop()
    {
        lifetime?.Cancel();
        RequestAdditionalTime(30000);
        try { work?.GetAwaiter().GetResult(); }
        catch (OperationCanceledException) { }
        catch { ExitCode = 1; }
        lifetime?.Dispose();
    }
}
