using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace DriveLedger.App;

// Pin program destinations and directories through mutation. Observe mutable Data files without blocking collector replacement.
public sealed class InstallSession : IDisposable
{
    public const string ContainerReceipt = "installation.json", AppReceipt = "installed-files.json";
    public sealed record Receipt(int Schema, string Product, string AppDir, string DataDir, string Sid, string[] Files);
    readonly InstallPlan plan;
    readonly Dictionary<string, SafeFileHandle> pins = new(StringComparer.OrdinalIgnoreCase);
    readonly string container;
    string? shortcut;
    public Receipt? ExistingReceipt { get; private set; }
    InstallSession(InstallPlan plan) { this.plan = plan; container = Path.GetDirectoryName(plan.AppDir)!; }

    public static void ValidateExisting(InstallPlan plan)
    {
        using var session = new InstallSession(plan);
        session.Preflight();
    }
    public static InstallSession Open(InstallPlan plan, string? shortcut = null)
    {
        var session = new InstallSession(plan);
        try
        {
            session.Preflight();
            session.PinShortcut(shortcut);
            session.EnsureDirectory(session.container);
            session.EnsureDirectory(plan.AppDir);
            session.EnsureDirectory(plan.DataDir);
            return session;
        }
        catch { session.Dispose(); throw; }
    }
    public static InstallSession OpenExisting(InstallPlan plan, string? shortcut = null)
    {
        var session = new InstallSession(plan);
        try
        {
            session.Preflight();
            session.PinShortcut(shortcut);
            if (session.ExistingReceipt is null) throw new ArgumentException("没有可信安装记录");
            return session;
        }
        catch { session.Dispose(); throw; }
    }
    void PinShortcut(string? path)
    {
        if (path is null) return;
        shortcut = Path.GetFullPath(path);
        var parent = Path.GetDirectoryName(shortcut)!;
        PinAncestors(parent);
        if (!Directory.Exists(parent)) throw new ArgumentException("桌面目录不可用");
        if (GetFileAttributes(shortcut) != uint.MaxValue) Pin(shortcut, false);
    }
    public void PublishShortcut(Action<string> create)
    {
        if (shortcut is null) throw new InvalidOperationException("桌面目标未预检");
        var stage = Path.Combine(plan.AppDir, ".shortcut-" + Guid.NewGuid().ToString("N") + ".lnk");
        try
        {
            create(stage); // Staged in protected App, never written directly into the user-writable desktop.
            var desktopStage = Path.Combine(Path.GetDirectoryName(shortcut)!, ".driveledger-shortcut-" + Guid.NewGuid().ToString("N"));
            using var handle = CreateFile(desktopStage, 0x40010000, 0, IntPtr.Zero, 1, 0x80, IntPtr.Zero);
            if (handle.IsInvalid) throw new IOException("无法创建桌面临时快捷方式", new Win32Exception(Marshal.GetLastWin32Error()));
            using var output = new FileStream(handle, FileAccess.Write);
            try
            {
                using (var input = File.OpenRead(stage)) input.CopyTo(output);
                output.Flush(true);
                if (pins.Remove(shortcut, out var old)) old.Dispose();
                RenameByHandle(handle, shortcut); // Same desktop volume; the stage stays pinned until renamed.
            }
            finally
            {
                output.Dispose();
                if (File.Exists(desktopStage)) File.Delete(desktopStage);
            }
        }
        finally { if (File.Exists(stage)) File.Delete(stage); }
    }
    static void RenameByHandle(SafeFileHandle handle, string target)
    {
        var name = System.Text.Encoding.Unicode.GetBytes(target);
        var offset = Marshal.OffsetOf<RenameHeader>(nameof(RenameHeader.FirstCharacter)).ToInt32();
        var lengthOffset = Marshal.OffsetOf<RenameHeader>(nameof(RenameHeader.FileNameLength)).ToInt32();
        var size = checked(offset + name.Length + sizeof(char));
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            for (int i = 0; i < size; i++) Marshal.WriteByte(buffer, i, 0);
            Marshal.WriteInt32(buffer, 0, 1); // ReplaceIfExists, not POSIX delete semantics.
            // Exact UTF-16 bytes, excluding the additional zero WCHAR required by the Win32 path conversion.
            Marshal.WriteInt32(buffer, lengthOffset, name.Length);
            Marshal.Copy(name, 0, buffer + offset, name.Length);
            if (!SetFileInformationByHandle(handle, 3, buffer, (uint)size)) throw new IOException("快捷方式安全替换失败", new Win32Exception(Marshal.GetLastWin32Error()));
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
    public void RemoveShortcut()
    {
        if (shortcut is null) return;
        if (pins.Remove(shortcut, out var old)) old.Dispose();
        if (File.Exists(shortcut)) File.Delete(shortcut);
    }
    public void RemovePayloads(string runningExecutable)
    {
        foreach (var relative in ExistingReceipt!.Files.Append(AppReceipt))
        {
            var full = Path.Combine(plan.AppDir, relative);
            if (Same(full, runningExecutable)) continue;
            if (pins.Remove(full, out var old)) old.Dispose();
            if (File.Exists(full)) File.Delete(full);
        }
        var receipt = Path.Combine(container, ContainerReceipt);
        if (pins.Remove(receipt, out var metadata)) metadata.Dispose();
        File.Delete(receipt);
    }
    void Preflight()
    {
        PinAncestors(container); PinAncestors(plan.AppDir); PinAncestors(plan.DataDir);
        if (Directory.Exists(container))
        {
            CheckAcl(container, true);
            var metadata = Path.Combine(container, ContainerReceipt);
            if (File.Exists(metadata))
            {
                Pin(metadata, false); CheckAcl(metadata, false);
                ExistingReceipt = ReadReceipt(metadata, plan);
            }
            else if (Directory.EnumerateFileSystemEntries(container).Any())
                throw new ArgumentException("已有产品目录缺少可信安装记录；未更改文件或权限。");
            foreach (var item in Directory.EnumerateFileSystemEntries(container))
                if (!Same(item, plan.AppDir) && !Same(item, plan.DataDir) && !Same(item, metadata))
                    throw new ArgumentException("产品目录包含无关项目，不能接管权限。");
        }
        foreach (var path in new[] { plan.AppDir, plan.DataDir })
        {
            if (!Directory.Exists(path)) continue;
            if (ExistingReceipt is null && Directory.EnumerateFileSystemEntries(path).Any())
                throw new ArgumentException("已有目标目录没有可信安装归属。");
            ValidateTree(path, mutableData: Same(path, plan.DataDir));
        }
        if (ExistingReceipt is not null)
        {
            var path = Path.Combine(plan.AppDir, AppReceipt);
            if (!File.Exists(path) || !ReadReceipt(path, plan).Files.SequenceEqual(ExistingReceipt.Files, StringComparer.OrdinalIgnoreCase))
                throw new ArgumentException("受保护的两份安装记录缺失或不一致");
        }
    }
    void ValidateTree(string path, bool mutableData)
    {
        Pin(path, true); CheckAcl(path, true);
        foreach (var item in Directory.EnumerateFileSystemEntries(path))
        {
            try
            {
                bool directory = (File.GetAttributes(item) & FileAttributes.Directory) != 0;
                if (mutableData && !directory) ObserveDataFile(item);
                else
                {
                    Pin(item, directory); CheckAcl(item, directory);
                    if (directory) ValidateTree(item, mutableData);
                }
            }
            catch (FileNotFoundException) when (mutableData) { }
            catch (DirectoryNotFoundException) when (mutableData) { }
        }
    }
    void ObserveDataFile(string path)
    {
        // Metadata-only access + delete sharing does not interfere even with an exclusive writer.
        // The protected read-only Data directory prevents an ordinary account from substituting a new entry.
        using var handle = CreateFile(path, 0x20080, 7, IntPtr.Zero, 3, 0x00200000 | 0x02000000, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            if (error is 2 or 3) return; // A collector temporary file disappeared between enumeration and observation.
            throw new IOException("无法观察数据文件: " + path, new Win32Exception(error));
        }
        ValidateHandle(handle, path, false);
        var result = GetSecurityInfo(handle, 1, 5, out _, out _, out _, out _, out var descriptor);
        if (result != 0) throw new IOException("无法读取数据文件权限: " + path, new Win32Exception((int)result));
        try
        {
            var bytes = new byte[checked((int)GetSecurityDescriptorLength(descriptor))];
            Marshal.Copy(descriptor, bytes, 0, bytes.Length);
            var acl = new FileSecurity();
            acl.SetSecurityDescriptorBinaryForm(bytes, AccessControlSections.Owner | AccessControlSections.Access);
            CheckAcl(acl, path); // Validate the opened object, never a path that could now name the next collector version.
        }
        finally { LocalFree(descriptor); }
    }
    public static Receipt ReadReceipt(string path, InstallPlan plan)
    {
        Receipt value;
        try { value = JsonSerializer.Deserialize<Receipt>(File.ReadAllText(path)) ?? throw new ArgumentException("空安装记录"); }
        catch (JsonException e) { throw new ArgumentException("安装记录损坏", e); }
        if (value.Schema != 1 || value.Product != "CDriveSpaceMonitor" || value.Sid != plan.Sid || !Same(value.AppDir, plan.AppDir) || !Same(value.DataDir, plan.DataDir) || value.Files is null)
            throw new ArgumentException("安装记录的产品、路径或账户不匹配");
        var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in value.Files)
            if (!SafeRelative(name) || !unique.Add(name)) throw new ArgumentException("安装记录包含不安全文件路径");
        return value;
    }
    static bool Same(string? a, string b) => a is not null && string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
    static bool SafeRelative(string? name) => !string.IsNullOrWhiteSpace(name) && !Path.IsPathFullyQualified(name) && !name.Contains(':') && name.Split('\\', '/').All(p => p.Length > 0 && p is not "." and not ".." && !p.EndsWith('.') && !p.EndsWith(' ') && p.IndexOfAny(Path.GetInvalidFileNameChars()) < 0) && !string.Equals(name, AppReceipt, StringComparison.OrdinalIgnoreCase);
    void PinAncestors(string path)
    {
        var parent = Path.GetDirectoryName(path);
        if (parent is not null && !Same(parent, path)) PinAncestors(parent);
        var attributes = GetFileAttributes(path);
        if (attributes != uint.MaxValue)
        {
            if ((attributes & 0x10) == 0) throw new ArgumentException("目录路径被文件占用");
            Pin(path, true);
        }
    }
    void Pin(string path, bool directory)
    {
        path = Path.GetFullPath(path);
        if (pins.ContainsKey(path)) return;
        // Include READ_DATA/LIST_DIRECTORY: metadata-only opens do not enforce delete sharing.
        var handle = CreateFile(path, 0x20081, 3, IntPtr.Zero, 3, 0x00200000 | 0x02000000, IntPtr.Zero);
        if (handle.IsInvalid) { handle.Dispose(); throw new IOException("无法锁定安装路径: " + path, new Win32Exception(Marshal.GetLastWin32Error())); }
        try
        {
            ValidateHandle(handle, path, directory);
            pins.Add(path, handle);
        }
        catch { handle.Dispose(); throw; }
    }
    static void ValidateHandle(SafeFileHandle handle, string path, bool directory)
    {
        if (!GetFileInformationByHandle(handle, out var info)) throw new Win32Exception(Marshal.GetLastWin32Error());
        if ((info.Attributes & 0x400) != 0 || ((info.Attributes & 0x10) != 0) != directory || (!directory && info.Links != 1))
            throw new ArgumentException("目标包含重解析点或多重硬链接: " + path);
    }
    void EnsureDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            if (!pins.ContainsKey(Path.GetFullPath(path))) throw new IOException("安全创建期间目录名称被占用，已停止。");
            return;
        }
        var parent = Path.GetDirectoryName(path)!;
        if (!pins.ContainsKey(Path.GetFullPath(parent))) EnsureDirectory(parent);
        var sddl = $"O:BAG:BAD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;0x1200a9;;;{plan.Sid})";
        if (!ConvertStringSecurityDescriptorToSecurityDescriptor(sddl, 1, out var descriptor, out _)) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var security = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), Descriptor = descriptor };
            if (!CreateDirectory(path, ref security)) throw new IOException("安全创建目录失败: " + path, new Win32Exception(Marshal.GetLastWin32Error()));
        }
        finally { LocalFree(descriptor); }
        Pin(path, true); CheckAcl(path, true);
    }
    void CheckAcl(string path, bool directory)
    {
        FileSystemSecurity acl = directory ? new DirectoryInfo(path).GetAccessControl() : new FileInfo(path).GetAccessControl();
        CheckAcl(acl, path);
    }
    static void CheckAcl(FileSystemSecurity acl, string path)
    {
        var owner = (acl.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier)?.Value;
        if (owner is not "S-1-5-18" and not "S-1-5-32-544") throw new ArgumentException("安装目标所有者不可信: " + path);
        const FileSystemRights writes = FileSystemRights.Write | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
        foreach (FileSystemAccessRule rule in acl.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType != AccessControlType.Allow || (rule.PropagationFlags & PropagationFlags.InheritOnly) != 0) continue;
            var who = ((SecurityIdentifier)rule.IdentityReference).Value;
            if (who is not "S-1-5-18" and not "S-1-5-32-544" && (rule.FileSystemRights & writes) != 0)
                throw new ArgumentException("安装目标可被普通账户改写或接管: " + path);
        }
    }
    public void ReplacePayload(string source, string relative)
    {
        if (!SafeRelative(relative)) throw new ArgumentException("无效载荷路径");
        var target = Path.GetFullPath(Path.Combine(plan.AppDir, relative));
        EnsureDirectory(Path.GetDirectoryName(target)!);
        AtomicWrite(target, stream => { using var input = File.OpenRead(source); input.CopyTo(stream); });
    }
    public void CommitReceipt(IReadOnlyList<string> files)
    {
        foreach (var relative in files) if (!SafeRelative(relative)) throw new ArgumentException("无效载荷路径");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new Receipt(1, "CDriveSpaceMonitor", plan.AppDir, plan.DataDir, plan.Sid, files.ToArray()));
        AtomicWrite(Path.Combine(plan.AppDir, AppReceipt), stream => stream.Write(bytes));
        AtomicWrite(Path.Combine(container, ContainerReceipt), stream => stream.Write(bytes));
    }
    void AtomicWrite(string target, Action<FileStream> write)
    {
        var stage = Path.Combine(Path.GetDirectoryName(target)!, ".install-" + Guid.NewGuid().ToString("N"));
        try
        {
            // CreateNew cannot follow a precreated link; parent is pinned and admin-write-only.
            using (var stream = new FileStream(stage, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { write(stream); stream.Flush(true); }
            if (pins.Remove(target, out var old)) old.Dispose();
            File.Move(stage, target, overwrite: true); // Replace entry, never overwrite its target.
            Pin(target, false); CheckAcl(target, false);
        }
        finally { if (File.Exists(stage)) File.Delete(stage); }
    }
    public void Dispose() { foreach (var handle in pins.Values.Reverse()) handle.Dispose(); pins.Clear(); }
    [StructLayout(LayoutKind.Sequential)] struct SecurityAttributes { public int Length; public IntPtr Descriptor; public int Inherit; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] struct RenameHeader { public uint Replace; public IntPtr RootDirectory; public uint FileNameLength; public char FirstCharacter; }
    [StructLayout(LayoutKind.Sequential)] struct FileInfoNative { public uint Attributes; public System.Runtime.InteropServices.ComTypes.FILETIME Creation, Access, Write; public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")] static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInfoNative info);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool SetFileInformationByHandle(SafeFileHandle handle, int informationClass, IntPtr information, uint size);
    [DllImport("advapi32.dll", SetLastError = true)] static extern uint GetSecurityInfo(SafeFileHandle handle, int objectType, uint securityInformation, out IntPtr owner, out IntPtr group, out IntPtr dacl, out IntPtr sacl, out IntPtr descriptor);
    [DllImport("advapi32.dll")] static extern uint GetSecurityDescriptorLength(IntPtr descriptor);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetFileAttributesW")] static extern uint GetFileAttributes(string path);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateDirectoryW")] static extern bool CreateDirectory(string path, ref SecurityAttributes security);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "ConvertStringSecurityDescriptorToSecurityDescriptorW")] static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(string sddl, uint revision, out IntPtr descriptor, out uint size);
    [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr memory);
}
