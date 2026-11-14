using DriveLedger.Core;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace DriveLedger.Windows;
public enum ControlCommand
{
    Status,
    Pause,
    Resume,
    Rescan
}

public sealed record ControlResponse(bool Success, CollectorStatus? Status, string? Error = null);
public sealed class ControlPipeServer(Collector collector, MonitorStore store, string installAccountSid, string pipeName = "DriveLedger.Control")
{
    public async Task RunAsync(CancellationToken token)
    {
        _ = new SecurityIdentifier(installAccountSid);
        ValidateName(pipeName);
        using var pipe = Create(pipeName, installAccountSid);
        while (!token.IsCancellationRequested)
        {
            try
            {
                await pipe.WaitForConnectionAsync(token);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                deadline.CancelAfter(TimeSpan.FromSeconds(3));
                var request = new byte[1];
                await pipe.ReadExactlyAsync(request, deadline.Token);
                ControlResponse response;
                switch ((ControlCommand)request[0])
                {
                    case ControlCommand.Status:
                        response = new(true, store.ReadCollectorStatus());
                        break;
                    case ControlCommand.Pause:
                        collector.Pause();
                        response = new(true, store.ReadCollectorStatus());
                        break;
                    case ControlCommand.Resume:
                        collector.Resume();
                        response = new(true, store.ReadCollectorStatus());
                        break;
                    case ControlCommand.Rescan:
                        collector.RequestScan();
                        response = new(true, store.ReadCollectorStatus());
                        break;
                    default:
                        response = new(false, null, "Unsupported command");
                        break;
                }

                var bytes = JsonSerializer.SerializeToUtf8Bytes(response);
                if (bytes.Length > 65536)
                    throw new InvalidDataException("Response too large");
                await pipe.WriteAsync(BitConverter.GetBytes(bytes.Length), deadline.Token);
                await pipe.WriteAsync(bytes, deadline.Token);
                await pipe.ReadExactlyAsync(new byte[1], deadline.Token);
            }
            catch (OperationCanceledException)when (!token.IsCancellationRequested)
            {
            }
            catch (IOException)
            {
            }
            catch (OperationCanceledException)when (token.IsCancellationRequested)
            {
                break;
            }
            finally
            {
                if (pipe.IsConnected)
                    pipe.Disconnect();
            }
        }
    }

    internal static void ValidateName(string name)
    {
        if (name.Length is < 1 or > 100 || name.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_')))
            throw new ArgumentException("Invalid local pipe name");
    }

    static NamedPipeServerStream Create(string name, string sid)
    {
        // Protected explicit DACL; network SID denied in addition to kernel remote rejection.
        var sddl = $"D:P(D;;GA;;;NU)(A;;GA;;;SY)(A;;GA;;;BA)(A;;GRGW;;;{sid})";
        if (!ConvertStringSecurityDescriptorToSecurityDescriptor(sddl, 1, out var descriptor, out _))
            throw NativeMetadata.Error();
        try
        {
            var security = new SecurityAttributes
            {
                Length = Marshal.SizeOf<SecurityAttributes>(),
                Descriptor = descriptor
            };
            var handle = CreateNamedPipe(@"\\.\pipe\" + name, 0x40080003, 8, 1, 65536, 64, 3000, ref security);
            if (handle.IsInvalid)
            {
                handle.Dispose();
                throw NativeMetadata.Error();
            }

            return new NamedPipeServerStream(PipeDirection.InOut, true, false, handle);
        }
        finally
        {
            LocalFree(descriptor);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct SecurityAttributes
    {
        public int Length;
        public IntPtr Descriptor;
        public int Inherit;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "ConvertStringSecurityDescriptorToSecurityDescriptorW")]
    static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(string sddl, uint revision, out IntPtr descriptor, out uint size);
    [DllImport("kernel32.dll")]
    static extern IntPtr LocalFree(IntPtr pointer);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateNamedPipeW")]
    static extern SafePipeHandle CreateNamedPipe(string name, uint open, uint mode, uint max, uint output, uint input, uint timeout, ref SecurityAttributes security);
}

public static class ControlPipeClient
{
    public static async Task<ControlResponse> SendAsync(ControlCommand command, CancellationToken cancellationToken = default, string pipeName = "DriveLedger.Control")
    {
        if (!Enum.IsDefined(command))
            throw new ArgumentOutOfRangeException(nameof(command));
        ControlPipeServer.ValidateName(pipeName);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
        await pipe.ConnectAsync(timeout.Token);
        await pipe.WriteAsync(new[] { (byte)command }, timeout.Token);
        var size = new byte[4];
        await pipe.ReadExactlyAsync(size, timeout.Token);
        var length = BitConverter.ToInt32(size);
        if (length is < 1 or > 65536)
            throw new InvalidDataException("Invalid control response length");
        var bytes = new byte[length];
        await pipe.ReadExactlyAsync(bytes, timeout.Token);
        await pipe.WriteAsync(new byte[] { 0 }, timeout.Token);
        return JsonSerializer.Deserialize<ControlResponse>(bytes) ?? throw new InvalidDataException("Empty control response");
    }
}
