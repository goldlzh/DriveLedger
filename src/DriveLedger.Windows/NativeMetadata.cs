using DriveLedger.Core;
using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace DriveLedger.Windows;
public static class NativeMetadata
{
    internal static Win32Exception Error() => new(Marshal.GetLastWin32Error());
    internal static SafeFileHandle Open(string path, uint access = 0)
    {
        var h = Native.CreateFile(path, access, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        if (h.IsInvalid)
        {
            var failure = Error();
            h.Dispose();
            throw failure;
        }

        return h;
    }

    public static string VolumeId(string root)
    {
        var b = new StringBuilder(128);
        if (!Native.GetVolumeNameForVolumeMountPoint(root, b, b.Capacity))
            throw Error();
        return b.ToString();
    }

    internal static string Identity(string volume, ReadOnlySpan<byte> id) => volume + Convert.ToHexString(id);
    internal static string Identity(string volume, ulong frn)
    {
        Span<byte> id = stackalloc byte[16];
        id.Clear();
        BinaryPrimitives.WriteUInt64LittleEndian(id, frn);
        return Identity(volume, id);
    }

    public static FileObservation Read(string path)
    {
        path = Path.GetFullPath(path);
        using var h = Open(path);
        return Read(h, path);
    }

    internal static FileObservation Read(SafeFileHandle h, string path)
    {
        byte[] Info(int kind, int size)
        {
            var b = new byte[size];
            if (!Native.GetFileInformationByHandleEx(h, kind, b, (uint)size))
                throw Error();
            return b;
        }

        var id = Info(18, 24);
        var standard = Info(1, 24);
        var basic = Info(0, 40);
        var attributes = BinaryPrimitives.ReadUInt32LittleEndian(basic.AsSpan(32));
        var directory = (attributes & 16) != 0;
        var reparse = (attributes & 0x400) != 0;
        long? allocated = BinaryPrimitives.ReadInt64LittleEndian(standard);
        if ((attributes & 0xA00) != 0)
        {
            var compressed = new byte[16];
            allocated = Native.GetFileInformationByHandleEx(h, 8, compressed, 16) ? BinaryPrimitives.ReadInt64LittleEndian(compressed) : null;
        }

        var volume = HandleVolume(h);
        path = FinalPath(h, 0);
        if (path.StartsWith(@"\\?\"))
            path = path[4..];
        var root = Path.GetPathRoot(path)!;
        if (!string.Equals(VolumeId(root), volume, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Resolved path drive does not identify the handle volume");
        var paths = new List<string>
        {
            path
        };
        if (!directory && !reparse)
        {
            uint size = 32768;
            var b = new StringBuilder((int)size);
            var find = Native.FindFirstFileName(path, 0, ref size, b);
            if (find != new IntPtr(-1))
            {
                try
                {
                    do
                    {
                        var link = Path.Combine(root, b.ToString().TrimStart('\\'));
                        // FindFirstFileName is path-based. Verify every returned name against
                        // the held object's volume serial + full ID before attributing a link.
                        using var linkHandle = Open(link);
                        var linkId = new byte[24];
                        if (!Native.GetFileInformationByHandleEx(linkHandle, 18, linkId, 24))
                            throw Error();
                        if (!id.AsSpan().SequenceEqual(linkId))
                            throw new IOException("Hard-link path changed during metadata observation");
                        paths.Add(link);
                        size = 32768;
                        b.Clear();
                    }
                    while (Native.FindNextFileName(find, ref size, b));
                    if (Marshal.GetLastWin32Error() != 38)
                        throw Error();
                }
                finally
                {
                    Native.FindClose(find);
                }
            }
            else
                throw Error();
        }

        return new(Identity(volume, id.AsSpan(8, 16)), Accounting.NormalizePaths(paths), allocated, BinaryPrimitives.ReadInt64LittleEndian(standard.AsSpan(8)), directory, reparse);
    }

    public static DiskSample Sample(string root)
    {
        if (!Native.GetDiskFreeSpaceEx(root, out _, out var total, out var free))
            throw Error();
        return new(DateTimeOffset.Now, VolumeId(root), checked((long)total), checked((long)free));
    }

    // Handles, not names, anchor traversal. Each depth owns one bounded native page.
    public static IEnumerable<FileObservation> Walk(string root, Action<Exception> error, CancellationToken token)
    {
        SafeFileHandle? opened = null;
        FileObservation rootItem;
        string volume;
        try
        {
            token.ThrowIfCancellationRequested();
            opened = Open(Path.GetFullPath(root), 1);
            rootItem = Read(opened, root);
            volume = HandleVolume(opened);
        }
        catch (Exception exception) when (exception is Win32Exception or IOException or UnauthorizedAccessException)
        {
            opened?.Dispose();
            error(exception);
            yield break;
        }
        using var rootHandle = opened;
        var stack = new Stack<DirectoryFrame>();
        if (rootItem.IsDirectory && !rootItem.IsReparsePoint)
            stack.Push(new DirectoryFrame(rootHandle, ownsHandle: false));
        try
        {
            yield return rootItem;
            while (stack.Count > 0)
            {
                token.ThrowIfCancellationRequested();
                DirectoryEntry? entry;
                try
                {
                    entry = stack.Peek().Next();
                }
                catch (Exception exception)when (exception is Win32Exception or IOException)
                {
                    error(exception);
                    stack.Pop().Dispose();
                    continue;
                }

                if (entry is null)
                {
                    stack.Pop().Dispose();
                    continue;
                }

                SafeFileHandle? child = null;
                FileObservation? item = null;
                try
                {
                    child = OpenReference(rootHandle, entry.Reference, entry.IsDirectory ? 1u : 0u);
                    item = Read(child, root);
                    if (!item.FileId.StartsWith(volume, StringComparison.OrdinalIgnoreCase))
                        throw new IOException("Directory entry resolved outside its anchored volume");
                    if (item.IsDirectory && !item.IsReparsePoint)
                    {
                        stack.Push(new DirectoryFrame(child, ownsHandle: true));
                        child = null; // The frame now owns the stable handle, before yielding.
                    }
                }
                catch (Exception exception)when (exception is Win32Exception or IOException or UnauthorizedAccessException)
                {
                    error(exception);
                    item = null;
                }
                finally
                {
                    child?.Dispose();
                }

                if (item is not null)
                    yield return item;
            }
        }
        finally
        {
            foreach (var frame in stack)
                frame.Dispose();
        }
    }

    internal static SafeFileHandle OpenReference(SafeFileHandle volumeHint, ulong reference, uint access)
    {
        var descriptor = new byte[24];
        BinaryPrimitives.WriteInt32LittleEndian(descriptor, 24);
        BinaryPrimitives.WriteUInt64LittleEndian(descriptor.AsSpan(8), reference);
        var handle = Native.OpenFileById(volumeHint, descriptor, access, 7, IntPtr.Zero, 0x02200000);
        if (handle.IsInvalid)
        {
            var error = Error();
            handle.Dispose();
            throw error;
        }

        return handle;
    }

    internal static string FinalPath(SafeFileHandle handle, uint flags)
    {
        var buffer = new StringBuilder(32768);
        var length = Native.GetFinalPathNameByHandle(handle, buffer, buffer.Capacity, flags);
        if (length == 0)
            throw Error();
        if (length >= buffer.Capacity)
            throw new IOException("Final metadata path exceeds supported bound");
        return buffer.ToString();
    }

    internal static string HandleVolume(SafeFileHandle handle)
    {
        var path = FinalPath(handle, 1); // VOLUME_NAME_GUID: actual handle volume, never lexical drive root.
        var end = path.IndexOf("}\\", StringComparison.Ordinal);
        if (!path.StartsWith(@"\\?\Volume{", StringComparison.OrdinalIgnoreCase) || end < 0)
            throw new IOException("A local volume GUID is required");
        return path[..(end + 2)];
    }

    sealed record DirectoryEntry(ulong Reference, bool IsDirectory);
    sealed class DirectoryFrame(SafeFileHandle handle, bool ownsHandle) : IDisposable
    {
        readonly byte[] buffer = new byte[65536];
        int offset = -1;
        bool exhausted;
        public DirectoryEntry? Next()
        {
            while (!exhausted)
            {
                if (offset < 0)
                {
                    Array.Clear(buffer);
                    if (!Native.GetFileInformationByHandleEx(handle, 10, buffer, (uint)buffer.Length))
                    {
                        var failure = Error();
                        if (failure.NativeErrorCode == 18) // ERROR_NO_MORE_FILES
                        {
                            exhausted = true;
                            return null;
                        }

                        throw failure;
                    }

                    offset = 0;
                }

                var record = buffer.AsSpan(offset);
                if (record.Length < 104)
                    throw new InvalidDataException("Truncated directory entry");
                var next = BinaryPrimitives.ReadUInt32LittleEndian(record);
                var nameLength = BinaryPrimitives.ReadUInt32LittleEndian(record[60..]);
                if (nameLength > record.Length - 104 || nameLength % 2 != 0 || (next != 0 && (next % 8 != 0 || next < 104 + nameLength || next > record.Length)))
                    throw new InvalidDataException("Invalid directory entry bounds");
                var name = Encoding.Unicode.GetString(record.Slice(104, (int)nameLength));
                var reference = BinaryPrimitives.ReadUInt64LittleEndian(record[96..]);
                var attributes = BinaryPrimitives.ReadUInt32LittleEndian(record[56..]);
                offset = next == 0 ? -1 : checked(offset + (int)next);
                if (name is not ("." or ".."))
                    return new DirectoryEntry(reference, (attributes & 16) != 0);
            }

            return null;
        }

        public void Dispose()
        {
            if (ownsHandle)
                handle.Dispose();
        }
    }
}

public sealed record UsnRecord(ulong FileReference, ulong ParentReference, long Usn, uint Reason, string Name);
public static class UsnParser
{
    public static IReadOnlyList<UsnRecord> Parse(ReadOnlySpan<byte> buffer)
    {
        var result = new List<UsnRecord>();
        while (!buffer.IsEmpty)
        {
            if (buffer.Length < 8)
                throw new InvalidDataException("Truncated USN header");
            var length = BinaryPrimitives.ReadUInt32LittleEndian(buffer);
            if (length < 60 || length > buffer.Length || length % 8 != 0)
                throw new InvalidDataException("Invalid USN length");
            if (BinaryPrimitives.ReadUInt16LittleEndian(buffer[4..]) != 2)
                throw new NotSupportedException("Only NTFS USN V2 is supported; reconciliation required");
            var r = buffer[..(int)length];
            var n = BinaryPrimitives.ReadUInt16LittleEndian(r[56..]);
            var offset = BinaryPrimitives.ReadUInt16LittleEndian(r[58..]);
            if (offset < 60 || offset % 2 != 0 || n % 2 != 0 || offset + n > length)
                throw new InvalidDataException("Invalid USN filename");
            result.Add(new(BinaryPrimitives.ReadUInt64LittleEndian(r[8..]), BinaryPrimitives.ReadUInt64LittleEndian(r[16..]), BinaryPrimitives.ReadInt64LittleEndian(r[24..]), BinaryPrimitives.ReadUInt32LittleEndian(r[40..]), Encoding.Unicode.GetString(r.Slice(offset, n))));
            buffer = buffer[(int)length..];
        }

        return result;
    }
}

internal static class Native
{
    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern SafeFileHandle OpenFileById(SafeFileHandle volume, byte[] descriptor, uint access, uint share, IntPtr security, uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetFinalPathNameByHandleW")]
    internal static extern uint GetFinalPathNameByHandle(SafeFileHandle file, StringBuilder path, int length, uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
    internal static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int kind, byte[] data, uint size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetVolumeNameForVolumeMountPointW")]
    internal static extern bool GetVolumeNameForVolumeMountPoint(string root, StringBuilder name, int size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "FindFirstFileNameW")]
    internal static extern IntPtr FindFirstFileName(string name, uint flags, ref uint length, StringBuilder link);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "FindNextFileNameW")]
    internal static extern bool FindNextFileName(IntPtr find, ref uint length, StringBuilder link);
    [DllImport("kernel32.dll")]
    internal static extern bool FindClose(IntPtr find);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetDiskFreeSpaceExW")]
    internal static extern bool GetDiskFreeSpaceEx(string root, out ulong available, out ulong total, out ulong free);
}
