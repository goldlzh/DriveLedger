using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Security.Principal;
using DriveLedger.Core;
namespace DriveLedger.App;
public sealed record Options(string Mode, string DataDir, string? FixtureRoot, string Pipe, string? Sid, string? Output, double Seconds, double SampleSeconds, double PollSeconds)
{
    public static Options Parse(string[] args)
    {
        string mode = "gui", data = @"D:\CDriveSpaceMonitor\Data", pipe = "DriveLedger.Control";
        string? fixture = null, sid = null, output = null;
        double seconds = 30, sample = 60, poll = 5;
        bool hasMode = false;
        bool explicitData = false;
        for (int i = 0; i < args.Length; i++)
        {
            string Value() => ++i < args.Length ? args[i] : throw new ArgumentException("参数缺少值");
            switch (args[i])
            {
                case "--service": case "--collect": case "--install": case "--uninstall": case "--diagnose":
                    if (hasMode) throw new ArgumentException("只能指定一个运行模式");
                    hasMode = true; mode = args[i][2..]; break;
                case "--data-dir": data = Full(Value()); explicitData = true; break;
                case "--fixture-root": fixture = Full(Value()); break;
                case "--pipe": pipe = Value(); break;
                case "--owner-sid": sid = Value(); break;
                case "--output": output = Full(Value()); break;
                case "--seconds": seconds = Number(Value()); break;
                case "--sample-seconds": sample = Number(Value()); break;
                case "--poll-seconds": poll = Number(Value()); break;
                default: throw new ArgumentException("未知参数: " + args[i]);
            }
        }
        if (pipe.Length is < 1 or > 100 || pipe.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_')))
            throw new ArgumentException("通信管道名称无效");
        if (mode == "collect" && fixture is null) throw new ArgumentException("--collect 仅支持隔离测试目录");
        if (fixture is not null && (!explicitData || mode is "install" or "uninstall" || Accounting.IsWithin(data, fixture) || Accounting.IsWithin(fixture, data)))
            throw new ArgumentException("测试必须指定独立于监测目录的数据目录，且不能用于正式安装/卸载");
        if (fixture is not null && (string.Equals(pipe, "DriveLedger.Control", StringComparison.OrdinalIgnoreCase) || !fixture.StartsWith(@"C:\", StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("测试目录必须在 C 盘，且须指定独立 --pipe");
        if (fixture is null && (sample != 60 || poll != 5 || pipe != "DriveLedger.Control"))
            throw new ArgumentException("短采样间隔和自定义管道仅供测试目录使用");
        if (poll > 60) throw new ArgumentException("日志间隔不能超过 60 秒");
        if (mode == "service" && sid is null) throw new ArgumentException("服务必须指定安装账户 --owner-sid");
        if (sid is not null) ValidateSid(sid);
        return new(mode, Full(data), fixture, pipe, sid, output, seconds, sample, poll);
    }
    static double Number(string value) => double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var n) && double.IsFinite(n) && n >= .1 && n <= 86400 ? n : throw new ArgumentException("时间间隔无效");
    internal static string Full(string path) => Path.IsPathFullyQualified(path) ? Path.GetFullPath(path).TrimEnd('\\') : throw new ArgumentException("必须使用绝对路径");
    internal static void ValidateSid(string sid)
    {
        var value = new SecurityIdentifier(sid);
        if (!value.IsAccountSid() || sid == "S-1-5-18") throw new ArgumentException("必须指定真实安装用户 SID");
    }
}
public static class Viewer
{
    public static ReportSnapshot? Read(string directory, DateTimeOffset from, DateTimeOffset to, string? folder = null)
    {
        var path = Path.Combine(Options.Full(directory), "ledger.db");
        if (!File.Exists(path)) return null;
        try { return new MonitorStore(path, readOnly: true).QueryReport(from, to, folder); }
        catch (Microsoft.Data.Sqlite.SqliteException error) when (error.SqliteErrorCode is 14 or 8)
        {
            throw new IOException("无法只读打开历史。若 WAL/SHM 辅助文件缺失，请启动采集服务以恢复辅助文件；查看器不会创建文件或申请写入权限。", error);
        }
    }
    public static string Export(ReportSnapshot report)
    {
        var csv = new StringBuilder();
        void Row(params object?[] values) => csv.AppendLine(Accounting.CsvRow(values));
        Row("类型", "路径/指标", "观测时刻", "上次观测", "新增字节", "释放字节", "迁入/迁移字节", "迁出字节", "分配字节", "逻辑字节", "其他链接");
        Row("整盘", "磁盘净变化", report.FirstObservation?.At.ToString("O"), report.LastObservation?.At.ToString("O"), report.DiskDeltaBytes?.ToString() ?? "未知");
        Row("整盘", "已归因净变化", report.From.ToString("O"), report.To.ToString("O"), report.NetBytes);
        Row("整盘", "尚未归因", null, null, report.UnattributedBytes?.ToString() ?? "未知");
        foreach (var f in report.Folders) Row("目录（包含子级，勿累加）", f.Path, null, null, f.AddedBytes, f.ReleasedBytes, f.MigratedInBytes, f.MigratedOutBytes);
        foreach (var f in report.Files) Row(Kind(f.Kind), f.Path ?? f.OldPath ?? "路径未解析", f.At.ToString("O"), f.PreviousObservedAt?.ToString("O"), f.AddedBytes, f.ReleasedBytes, f.MigratedBytes, null, f.AllocatedBytes, f.LogicalBytes, string.Join(" | ", f.Paths ?? []));
        foreach (var note in report.CoverageNotes) Row("覆盖说明", note.Message, note.Start.ToString("O"), note.End?.ToString("O"));
        Row("明细覆盖", report.FileDetailsComplete ? "所选窗口明细保留完整" : "部分明细已过30天保留期");
        return csv.ToString();
    }
    public static string Kind(string kind) => kind switch { "created" => "新增文件", "deleted" => "删除释放", "migration" => "路径迁移", "creation-confirmed" => "创建证据补齐", "observed" => "已有文件变化", _ => kind };
}
public sealed record InstallPlan(string Source, string AppDir, string DataDir, string Sid)
{
    public static InstallPlan Create(string source, string app, string data, string sid)
    {
        source = Options.Full(source); app = Options.Full(app); data = Options.Full(data);
        Options.ValidateSid(sid);
        foreach (var path in new[] { source, app, data })
        {
            if (path.Length <= 3 || path == Environment.GetFolderPath(Environment.SpecialFolder.Windows)) throw new ArgumentException("禁止使用根目录或 Windows 目录");
            for (var p = new DirectoryInfo(path); p is not null; p = p.Parent)
                if (p.Exists && (p.Attributes & FileAttributes.ReparsePoint) != 0) throw new ArgumentException("安装路径不能经过重解析点");
        }
        foreach (var path in new[] { app, data })
            foreach (var special in new[] { Environment.SpecialFolder.Windows, Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 })
            {
                var reserved = Environment.GetFolderPath(special);
                if (reserved.Length > 0 && Accounting.IsWithin(path, reserved)) throw new ArgumentException("禁止接管系统目录");
            }
        if (Accounting.IsWithin(app, data) || Accounting.IsWithin(data, app) || Accounting.IsWithin(source, app) || Accounting.IsWithin(app, source) || Accounting.IsWithin(source, data) || Accounting.IsWithin(data, source))
            throw new ArgumentException("源目录、程序目录和数据目录必须相互独立");
        var plan = new InstallPlan(source, app, data, sid);
        InstallSession.ValidateExisting(plan);
        return plan;
    }
}
