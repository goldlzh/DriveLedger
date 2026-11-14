using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.ServiceProcess;
namespace DriveLedger.App;
public static class Program
{
    [STAThread] public static int Main(string[] args)
    {
        Options? options = null;
        try
        {
            options = Options.Parse(args);
            switch (options.Mode)
            {
                case "service": ServiceBase.Run(new ServiceHost(options)); break;
                case "collect":
                    using (var stop = new CancellationTokenSource(TimeSpan.FromSeconds(options.Seconds)))
                        CollectionHost.RunAsync(options, stop.Token).GetAwaiter().GetResult();
                    break;
                case "install": Installer.Install(options); break;
                case "uninstall": Installer.Uninstall(); break;
                case "diagnose":
                    var report = Viewer.Read(options.DataDir, DateTimeOffset.Now.AddDays(-30), DateTimeOffset.Now);
                    WriteJson(options.Output, new { Success = true, DataDirectory = options.DataDir, DatabaseExists = report is not null, Report = report });
                    break;
                default:
                    using (var instance = ViewerInstance.Acquire(ViewerInstance.ScopeFor(options)))
                    {
                        if (!instance.IsPrimary) return instance.ActivateExisting() ? 0 : 1;
                        var application = new Application();
                        var window = new MainWindow(options);
                        instance.StartListening(() => window.Dispatcher.BeginInvoke(new Action(window.Open)));
                        application.Run(window);
                    }
                    break;
            }
            return 0;
        }
        catch (Exception error)
        {
            if (options?.Mode == "diagnose") WriteJson(options.Output, new { Success = false, Error = error.Message });
            else if (args.Length == 0 || Array.Exists(args, a => a is "--install" or "--uninstall"))
                MessageBox.Show(error.Message, "C盘空间账本", MessageBoxButton.OK, MessageBoxImage.Error);
            else if (options?.Output is { } output) WriteJson(output, new { Success = false, Error = error.Message });
            return 1;
        }
    }
    static void WriteJson(string? path, object value)
    {
        var json = JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true });
        if (path is null) Console.WriteLine(json);
        else File.WriteAllText(path, json, new UTF8Encoding(false));
    }
}
