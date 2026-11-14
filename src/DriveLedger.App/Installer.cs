using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text.Json;
using System.Windows;
using Microsoft.Win32;
namespace DriveLedger.App;

public static class Installer
{
    const string AppDir = @"D:\CDriveSpaceMonitor\App";
    const string Name = "CDriveSpaceMonitor";
    const string Marker = "installed-files.json";

    static bool Elevate(string mode, string sid, string? data = null)
    {
        if (new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator)) return false;
        var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas" };
        info.ArgumentList.Add(mode); info.ArgumentList.Add("--owner-sid"); info.ArgumentList.Add(sid);
        if (data is not null) { info.ArgumentList.Add("--data-dir"); info.ArgumentList.Add(data); }
        using var child = Process.Start(info) ?? throw new IOException("无法启动安装程序");
        child.WaitForExit();
        if (child.ExitCode != 0) throw new IOException("安装或卸载未完成");
        return true;
    }

    public static void Install(Options options)
    {
        var sid = options.Sid ?? WindowsIdentity.GetCurrent().User!.Value;
        if (!string.Equals(Path.GetDirectoryName(options.DataDir), Path.GetDirectoryName(AppDir), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("正式安装的数据目录必须是 D:\\CDriveSpaceMonitor 下独立于 App 的同级目录。");
        var plan = InstallPlan.Create(AppContext.BaseDirectory, AppDir, options.DataDir, sid);
        if (!Directory.Exists(Path.GetPathRoot(plan.DataDir)) || !Directory.Exists(@"D:\")) throw new IOException("D 盘或指定数据盘不可用；安装已停止。");
        if (Elevate("--install", sid, plan.DataDir)) return;
        var sourceFiles = EnumeratePayload(plan.Source).ToArray();
        if (!sourceFiles.Any(f => Path.GetFileName(f) == "DriveLedger.App.exe")) throw new IOException("完整发布包缺少程序文件");
        InstallationLifecycle.Apply(plan, sourceFiles,
            stop: () =>
            {
                using (var service = FindService())
                {
                    if (service is not null)
                    {
                        EnsureOwnedService();
                        Stop(service);
                        Run("sc.exe", "delete", Name);
                    }
                }
                WaitDeleted();
            },
            configure: session =>
            {
                var exe = Path.Combine(plan.AppDir, "DriveLedger.App.exe");
                var command = $"\"{exe}\" --service --data-dir \"{plan.DataDir}\" --owner-sid {sid}";
                Run("sc.exe", "create", Name, "binPath=", command, "start=", "auto", "obj=", "LocalSystem", "DisplayName=", "C盘空间账本采集服务");
                using (var startup = Registry.Users.CreateSubKey(sid + @"\Software\Microsoft\Windows\CurrentVersion\Run"))
                    startup.SetValue(Name, $"\"{exe}\" --data-dir \"{plan.DataDir}\"");
                CreateShortcut(session, exe, plan.DataDir);
            },
            start: () => Run("sc.exe", "start", Name),
            notify: () => MessageBox.Show("安装完成。后台采集已启动。请从桌面“C盘空间账本”快捷方式打开普通权限界面。首次索引可能需要较长时间。", "C盘空间账本"),
            shortcut: ShortcutPath(sid));
    }

    public static void Uninstall()
    {
        var receiptPath = Path.Combine(AppDir, Marker);
        if (!File.Exists(receiptPath)) throw new IOException("未发现受管安装记录；未更改服务或文件。");
        var receipt = JsonSerializer.Deserialize<InstallSession.Receipt>(File.ReadAllText(receiptPath)) ?? throw new IOException("安装记录损坏");
        Options.ValidateSid(receipt.Sid);
        var plan = new InstallPlan(AppDir, AppDir, receipt.DataDir, receipt.Sid);
        InstallSession.ValidateExisting(plan);
        if (Elevate("--uninstall", receipt.Sid)) return;
        using (var session = InstallSession.OpenExisting(plan, ShortcutPath(receipt.Sid)))
        {
            using (var service = FindService())
            {
                if (service is not null) { EnsureOwnedService(); Stop(service); Run("sc.exe", "delete", Name); }
            }
            WaitDeleted();
            using (var startup = Registry.Users.OpenSubKey(receipt.Sid + @"\Software\Microsoft\Windows\CurrentVersion\Run", writable: true))
                startup?.DeleteValue(Name, false);
            session.RemoveShortcut();
            session.RemovePayloads(Environment.ProcessPath!);
        }
        MessageBox.Show("服务和开机入口已移除，Data 历史数据已保留。若卸载程序本身位于 App 内，可在关闭后手动删除 App 文件夹。", "C盘空间账本");
    }
    static System.Collections.Generic.IEnumerable<string> EnumeratePayload(string root)
    {
        foreach (var item in Directory.EnumerateFileSystemEntries(root))
        {
            var flags = File.GetAttributes(item);
            if ((flags & FileAttributes.ReparsePoint) != 0) throw new IOException("程序或数据目录含重解析点，操作已停止: " + item);
            if ((flags & FileAttributes.Directory) != 0) { foreach (var child in EnumeratePayload(item)) yield return child; }
            else yield return item;
        }
    }
    static ServiceController? FindService() => ServiceController.GetServices().FirstOrDefault(s => s.ServiceName == Name);
    static void EnsureOwnedService()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + Name);
        if (key?.GetValue("ImagePath") is not string command || !command.StartsWith("\"" + Path.Combine(AppDir, "DriveLedger.App.exe") + "\" --service ", StringComparison.OrdinalIgnoreCase))
            throw new IOException("同名服务不是本安装拥有，操作已停止。");
    }
    static void Stop(ServiceController service)
    {
        service.Refresh();
        if (service.Status != ServiceControllerStatus.Stopped) { service.Stop(); service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(45)); }
    }
    static void WaitDeleted()
    {
        for (var i = 0; i < 100; i++)
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + Name);
            if (key is null) return;
            System.Threading.Thread.Sleep(100);
        }
        throw new IOException("服务删除尚未完成，请关闭服务管理窗口后重试。");
    }
    static void Run(string file, params string[] args)
    {
        var info = new ProcessStartInfo(file) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        using var process = Process.Start(info) ?? throw new IOException("无法启动 " + file);
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        if (process.ExitCode != 0) throw new IOException(file + ": " + stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult());
    }
    static string ShortcutPath(string sid)
    {
        using var profile = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\" + sid);
        var home = profile?.GetValue("ProfileImagePath") as string ?? throw new IOException("找不到安装账户配置文件");
        using var folders = Registry.Users.OpenSubKey(sid + @"\Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders");
        var desktop = folders?.GetValue("Desktop", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string ?? Path.Combine(home, "Desktop");
        desktop = desktop.Replace("%USERPROFILE%", home, StringComparison.OrdinalIgnoreCase);
        if (desktop.Contains('%')) throw new IOException("无法解析安装账户桌面路径");
        return Path.Combine(desktop, "C盘空间账本.lnk");
    }
    static void CreateShortcut(InstallSession session, string exe, string data)
    {
        var type = Type.GetTypeFromProgID("WScript.Shell") ?? throw new IOException("快捷方式组件不可用");
        dynamic shell = Activator.CreateInstance(type)!;
        session.PublishShortcut(path =>
        {
            dynamic shortcut = shell.CreateShortcut(path);
            shortcut.TargetPath = exe; shortcut.Arguments = $"--data-dir \"{data}\""; shortcut.WorkingDirectory = AppDir; shortcut.Save();
        });
    }
}
