using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using DriveLedger.Core;
using DriveLedger.Windows;
using Forms = System.Windows.Forms;
namespace DriveLedger.App;

public partial class MainWindow : Window
{
    readonly Options options;
    readonly SemaphoreSlim refreshGate = new(1);
    readonly Forms.NotifyIcon tray;
    readonly DispatcherTimer timer;
    ReportSnapshot? report;
    string? folder;
    bool exiting;
    int requestVersion;
    public MainWindow(Options options)
    {
        this.options = options;
        InitializeComponent();
        FromDate.SelectedDate = DateTime.Today;
        ToDate.SelectedDate = DateTime.Today;
        tray = new Forms.NotifyIcon { Text = "C盘空间账本", Icon = System.Drawing.SystemIcons.Information, Visible = true };
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("打开账本", null, (_, _) => Dispatcher.Invoke(Open));
        menu.Items.Add("退出查看器（采集服务继续）", null, (_, _) => Dispatcher.Invoke(() => { exiting = true; Close(); }));
        tray.ContextMenuStrip = menu;
        tray.DoubleClick += (_, _) => Dispatcher.Invoke(Open);
        Closing += (_, e) => { if (!exiting) { e.Cancel = true; Hide(); } };
        timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        Closed += (_, _) => { tray.Dispose(); timer.Stop(); };
        timer.Tick += async (_, _) => { if (IsVisible) await RefreshAsync(); };
        Loaded += async (_, _) => { timer.Start(); await RefreshAsync(); };
    }
    void Open() { Show(); WindowState = WindowState.Normal; Activate(); }
    async Task RefreshAsync()
    {
        var version = ++requestVersion;
        var from = FromDate.SelectedDate ?? DateTime.Today;
        var to = (ToDate.SelectedDate ?? DateTime.Today).AddDays(1).AddTicks(-1);
        if (from > to) { StatusText.Text = "起始日期不能晚于结束日期。"; return; }
        var selectedFolder = folder;
        await refreshGate.WaitAsync();
        try
        {
            if (version != requestVersion) return;
            StatusText.Text = "正在读取统计…";
            var next = await Task.Run(() => Viewer.Read(options.DataDir, new DateTimeOffset(from), new DateTimeOffset(to), selectedFolder));
            if (version != requestVersion) return;
            report = next;
            if (next is null)
            {
                DiskTotal.Text = FileTotal.Text = UnknownTotal.Text = "未知";
                GrossTotal.Text = "等待采集";
                StatusText.Text = "尚无数据库。请运行安装命令建立后台服务；此查看器不会创建数据库或申请管理员权限。";
                WindowText.Text = "数据目录：" + options.DataDir + "（数据盘不可用时不会改用 C 盘）";
                Folders.ItemsSource = null; Files.ItemsSource = null; Trend.Children.Clear();
                return;
            }
            var rows = await Task.Run(() => (
                Folders: next.Folders.OrderByDescending(f => f.NetBytes).Take(5000).Select(f => new FolderRow(f.Path, f.NetBytes, f.AddedBytes, f.ReleasedBytes, f.MigratedInBytes, f.MigratedOutBytes)).ToArray(),
                Files: next.Files.OrderByDescending(f => f.AddedBytes - f.ReleasedBytes).Take(5000).Select(f => new FileRow(f, f.Path ?? f.OldPath ?? "路径未解析（已计入总账）", Viewer.Kind(f.Kind), Bytes(f.AddedBytes), Bytes(f.ReleasedBytes), Time(f.At), Time(f.PreviousObservedAt))).ToArray()));
            if (version != requestVersion) return;
            Apply(next, rows.Folders, rows.Files);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            try
            {
                var live = await ControlPipeClient.SendAsync(ControlCommand.Status, deadline.Token, options.Pipe);
                if (version == requestVersion) StatusText.Text += live.Success ? " · 通信正常" : " · 服务拒绝请求：" + live.Error;
            }
            catch (Exception e) when (e is IOException or OperationCanceledException or UnauthorizedAccessException)
            {
                if (version == requestVersion) StatusText.Text += " · 后台服务未连接，当前显示已保存历史。";
            }
        }
        catch (Exception e) { StatusText.Text = "读取失败：" + e.Message + "；未创建替代数据库。"; }
        finally { refreshGate.Release(); }
    }
    static string Bytes(long? n) => n.HasValue ? Accounting.DisplayBytes(n.Value) : "未知";
    static string Time(DateTimeOffset? at) => at?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz") ?? "未知";
    void Apply(ReportSnapshot r, FolderRow[] folderRows, FileRow[] fileRows)
    {
        DiskTotal.Text = Bytes(r.DiskDeltaBytes); FileTotal.Text = Bytes(r.NetBytes); UnknownTotal.Text = Bytes(r.UnattributedBytes);
        GrossTotal.Text = Bytes(r.AddedBytes) + " / " + Bytes(r.ReleasedBytes);
        StatusText.Text = $"{r.Status.State} · 基准{(r.Status.BaselineReady ? "已就绪" : "未完成")} · 已观察 {r.Status.FilesObserved:N0} · 访问失败 {r.Status.AccessFailures:N0} · 状态更新 {Time(r.Status.UpdatedAt)}";
        if (r.Status.State == "降级运行") StatusText.Foreground = Brushes.DarkRed;
        else StatusText.Foreground = Brushes.DarkSlateGray;
        WindowText.Text = $"实际比较：{Time(r.FirstObservation?.At)} → {Time(r.LastObservation?.At)}。" + (r.DiskDeltaBytes is null ? "有效磁盘样本不足或卷身份不同，磁盘变化未知。" : "正数为多占用，负数为释放。") + "\n" + Explain(r.Status.Message);
        Folders.ItemsSource = folderRows;
        FolderText.Text = (folder ?? "整盘目录") + $" · {r.Folders.Count:N0} 个目录，最多显示前5000项。双击下钻；各级包含子级，不可累加，净变化含迁移。";
        Files.ItemsSource = fileRows;
        DetailText.Text = $"文件明细 {r.Files.Count:N0} 项，最多显示前5000项；CSV 导出所有查询明细。" + (r.FileDetailsComplete ? "所选窗口明细保留完整。" : $"部分明细已超过30天保留期，目录总账仍保留；明细起点 {Time(r.FileDetailsAvailableFrom)}。");
        Coverage.Text = "采集状态：" + r.Status.State + "\n" + Explain(r.Status.Message) + "\n\n" + string.Join("\n\n", r.CoverageNotes.Select(n => $"{Time(n.Start)} → {Time(n.End)}\n{CoverageName(n.Code)}：{Explain(n.Message)}"));
        DrawTrend();
    }
    static string Explain(string? message)
    {
        if (message?.Contains("active-set continuity degraded", StringComparison.Ordinal) == true)
            return "活动文件跟踪连续性中断：部分此前已打开的写入文件无法保证完整跟踪。手动核对不能消除此历史缺口。\n技术记录：" + message;
        return message ?? "";
    }
    static string CoverageName(string code) => code switch
    {
        "sample-gap" => "磁盘采样缺口", "observation-window" => "实际观测窗口", "baseline-incomplete" => "初始索引未完成",
        "volume-change" => "磁盘身份变化", "active-set" => "活动文件连续性缺失", "metadata-unavailable" => "元数据暂不可读",
        "creation-confirmed" => "创建证据补齐", "startup" => "采集停止后的重启区间", "paused" => "用户暂停区间",
        "journal-gap" => "变更日志连续性缺口", "journal-unavailable" => "变更日志不可用，依赖核对扫描",
        "incomplete-scan" => "核对扫描未完成，保留未见文件", "active-set-unavailable" => "活动文件跟踪记录缺失或损坏",
        "active-set-recovered" => "已证实新内核会话，活动文件跟踪恢复", "dirty-overflow" => "活动文件超过跟踪上限，覆盖降级",
        "creation-allocation-unavailable" => "有创建证据但分配大小未知", _ => code
    };
    void DrawTrend()
    {
        Trend.Children.Clear();
        if (report is null || report.Samples.Count == 0 || Trend.ActualWidth < 20) return;
        var samples = report.Samples.OrderBy(s => s.At).ToArray();
        double low = samples.Min(s => s.TotalBytes - s.FreeBytes), high = samples.Max(s => s.TotalBytes - s.FreeBytes);
        double range = Math.Max(1, high - low), span = Math.Max(1, (samples[^1].At - samples[0].At).TotalSeconds);
        TrendLabel.Text = $"整盘已用空间 {Bytes((long)low)} — {Bytes((long)high)} · {samples.Length:N0} 个实际样本 · 超过2分钟或卷变化处断开";
        Polyline? line = null;
        double lastX = -100;
        for (int i = 0; i < samples.Length; i++)
        {
            var s = samples[i];
            bool broken = i == 0 || s.At - samples[i - 1].At > TimeSpan.FromMinutes(2) || s.VolumeId != samples[i - 1].VolumeId;
            if (broken) { line = new Polyline { Stroke = Brushes.SteelBlue, StrokeThickness = 2 }; Trend.Children.Add(line); lastX = -100; }
            double x = 5 + (s.At - samples[0].At).TotalSeconds / span * (Trend.ActualWidth - 10);
            double y = 7 + (high - (s.TotalBytes - s.FreeBytes)) / range * Math.Max(1, Trend.ActualHeight - 14);
            bool endpoint = i == samples.Length - 1 || samples[i + 1].At - s.At > TimeSpan.FromMinutes(2) || samples[i + 1].VolumeId != s.VolumeId;
            if (x - lastX >= 2 || endpoint) { line!.Points.Add(new Point(x, y)); lastX = x; }
            if (broken || endpoint) Trend.Children.Add(new Ellipse { Width = 4, Height = 4, Fill = Brushes.SteelBlue, Margin = new Thickness(x - 2, y - 2, 0, 0) });
        }
    }
    async Task Send(ControlCommand command)
    {
        var label = command switch { ControlCommand.Pause => "暂停", ControlCommand.Resume => "恢复", ControlCommand.Rescan => "核对", _ => "控制" };
        StatusText.Text = "正在发送" + label + "请求…";
        try
        {
            var result = await ControlPipeClient.SendAsync(command, pipeName: options.Pipe);
            if (!result.Success) throw new IOException(result.Error);
            await RefreshAsync();
            StatusText.Text += " · " + label + "请求已接收，状态将在采集周期内更新。";
        }
        catch (Exception e) { MessageBox.Show("服务控制失败：" + e.Message + "\n请检查后台服务是否运行。", "C盘空间账本"); }
    }
    async void RefreshClick(object sender, RoutedEventArgs e) => await RefreshAsync();
    async void TodayClick(object sender, RoutedEventArgs e) { FromDate.SelectedDate = ToDate.SelectedDate = DateTime.Today; await RefreshAsync(); }
    async void WeekClick(object sender, RoutedEventArgs e) { FromDate.SelectedDate = DateTime.Today.AddDays(-6); ToDate.SelectedDate = DateTime.Today; await RefreshAsync(); }
    async void RootClick(object sender, RoutedEventArgs e) { folder = null; await RefreshAsync(); }
    async void DrillClick(object sender, MouseButtonEventArgs e) { if (Folders.SelectedItem is FolderRow row) { folder = row.Path; await RefreshAsync(); } }
    async void PauseClick(object sender, RoutedEventArgs e) => await Send(ControlCommand.Pause);
    async void ResumeClick(object sender, RoutedEventArgs e) => await Send(ControlCommand.Resume);
    async void ScanClick(object sender, RoutedEventArgs e) => await Send(ControlCommand.Rescan);
    void TrendSizeChanged(object sender, SizeChangedEventArgs e) => DrawTrend();
    void FileSelected(object sender, SelectionChangedEventArgs e)
    {
        if (Files.SelectedItem is not FileRow row) return;
        var f = row.Source;
        FileInfo.Text = $"{row.Kind} · {row.Path}\n原路径：{f.OldPath ?? "无"} · 迁移：{Bytes(f.MigratedBytes)} · 当前分配：{Bytes(f.AllocatedBytes)} · 逻辑大小：{Bytes(f.LogicalBytes)}\n实际观测：{row.Previous} → {row.At} · 全部硬链接：{string.Join(" | ", f.Paths ?? [])}\n身份：{f.FileId}";
    }
    async void ExportClick(object sender, RoutedEventArgs e)
    {
        if (report is null) { MessageBox.Show("尚无可导出的统计。", "C盘空间账本"); return; }
        var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "CSV 文件|*.csv", FileName = "C盘空间账本.csv" };
        if (dialog.ShowDialog(this) != true) return;
        var snapshot = report;
        try { await Task.Run(() => File.WriteAllText(dialog.FileName, Viewer.Export(snapshot), new UTF8Encoding(true))); MessageBox.Show("已导出所选窗口和目录的全部查询明细。", "C盘空间账本"); }
        catch (Exception error) { MessageBox.Show("导出失败：" + error.Message, "C盘空间账本"); }
    }
    sealed record FolderRow(string Path, long NetBytes, long AddedBytes, long ReleasedBytes, long InBytes, long OutBytes)
    {
        public string Net => Bytes(NetBytes);
        public string Added => Bytes(AddedBytes);
        public string Released => Bytes(ReleasedBytes);
        public string In => Bytes(InBytes);
        public string Out => Bytes(OutBytes);
    }
    sealed record FileRow(FileChange Source, string Path, string Kind, string Added, string Released, string At, string Previous)
    {
        public long AddedBytes => Source.AddedBytes;
        public long ReleasedBytes => Source.ReleasedBytes;
    }
}
