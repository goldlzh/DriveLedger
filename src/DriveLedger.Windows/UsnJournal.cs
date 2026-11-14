using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using DriveLedger.Core;

namespace DriveLedger.Windows;
public sealed record JournalState(ulong JournalId, long FirstUsn, long NextUsn, long LowestValidUsn);
public sealed record JournalPage(long NextUsn, IReadOnlyList<UsnRecord> Records);
public sealed class UsnJournal : IDisposable
{
    readonly SafeFileHandle handle;
    readonly string root;
    public string VolumeId { get; }

    public UsnJournal(string volume)
    {
        root = volume;
        VolumeId = NativeMetadata.VolumeId(volume);
        handle = NativeMetadata.Open(@"\\.\" + volume.TrimEnd('\\'), 0x80000000);
    }

    byte[] Io(uint code, byte[] input, int size)
    {
        var output = new byte[size];
        if (!DeviceIoControl(handle, code, input, input.Length, output, size, out var returned, IntPtr.Zero))
            throw NativeMetadata.Error();
        return output[..returned];
    }

    public JournalState Query()
    {
        var b = Io(0x900f4, [], 128);
        if (b.Length < 56)
            throw new InvalidDataException("Truncated journal state");
        return new(BinaryPrimitives.ReadUInt64LittleEndian(b), BinaryPrimitives.ReadInt64LittleEndian(b.AsSpan(8)), BinaryPrimitives.ReadInt64LittleEndian(b.AsSpan(16)), BinaryPrimitives.ReadInt64LittleEndian(b.AsSpan(24)));
    }

    public JournalPage Read(long next, ulong journalId)
    {
        var input = new byte[48];
        BinaryPrimitives.WriteInt64LittleEndian(input, next);
        BinaryPrimitives.WriteUInt32LittleEndian(input.AsSpan(8), uint.MaxValue);
        BinaryPrimitives.WriteUInt64LittleEndian(input.AsSpan(32), journalId);
        input[40] = 2;
        input[42] = 2;
        var b = Io(0x900bb, input, 65536);
        if (b.Length < 8)
            throw new InvalidDataException("Truncated journal page");
        return new(BinaryPrimitives.ReadInt64LittleEndian(b), UsnParser.Parse(b.AsSpan(8)));
    }

    public FileObservation Resolve(ulong reference)
    {
        var descriptor = new byte[24];
        BinaryPrimitives.WriteInt32LittleEndian(descriptor, 24);
        BinaryPrimitives.WriteUInt64LittleEndian(descriptor.AsSpan(8), reference);
        using var file = OpenFileById(handle, descriptor, 0, 7, IntPtr.Zero, 0x02200000);
        if (file.IsInvalid)
            throw NativeMetadata.Error();
        var path = new StringBuilder(32768);
        var n = GetFinalPathNameByHandle(file, path, path.Capacity, 0);
        if (n == 0 || n >= path.Capacity)
            throw NativeMetadata.Error();
        var name = path.ToString();
        if (name.StartsWith(@"\\?\"))
            name = name[4..];
        if (!string.Equals(Path.GetPathRoot(name), root, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Resolved path is outside monitored volume");
        return NativeMetadata.Read(file, name);
    }

    public string FileId(ulong reference) => NativeMetadata.Identity(VolumeId, reference);
    public void Dispose() => handle.Dispose();
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool DeviceIoControl(SafeFileHandle h, uint code, byte[] input, int inputSize, byte[] output, int outputSize, out int returned, IntPtr overlapped);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern SafeFileHandle OpenFileById(SafeFileHandle volume, byte[] id, uint access, uint share, IntPtr security, uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetFinalPathNameByHandleW")]
    static extern uint GetFinalPathNameByHandle(SafeFileHandle file, StringBuilder path, int length, uint flags);
}
