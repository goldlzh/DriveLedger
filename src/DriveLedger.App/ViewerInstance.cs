using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace DriveLedger.App;

public sealed class ViewerInstance : IDisposable
{
    readonly Mutex mutex;
    readonly CancellationTokenSource stop = new();
    readonly NamedPipeServerStream? server;
    Task? listener;
    bool disposed;
    public bool IsPrimary { get; }
    public string PipeName { get; }

    public static string ScopeFor(Options options)
    {
        using var identity = WindowsIdentity.GetCurrent();
        using var process = Process.GetCurrentProcess();
        var scope = identity.User!.Value + ":" + process.SessionId;
        if (options.FixtureRoot is { } fixture)
            scope += ":fixture:" + fixture.ToUpperInvariant() + ":" + options.Pipe.ToUpperInvariant();
        return scope;
    }

    public static ViewerInstance Acquire(string scope) => new(scope);
    ViewerInstance(string scope)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        var name = "DriveLedger.Viewer." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(scope)))[..32];
        PipeName = name + ".Activate";
        mutex = new Mutex(false, @"Local\" + name);
        try { IsPrimary = mutex.WaitOne(0); }
        catch (AbandonedMutexException) { IsPrimary = true; }
        if (!IsPrimary) return;
        try
        {
            server = new NamedPipeServerStream(PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly | PipeOptions.FirstPipeInstance, 64, 64);
        }
        catch
        {
            mutex.ReleaseMutex(); mutex.Dispose(); stop.Dispose(); throw;
        }
    }

    public void StartListening(Action activate)
    {
        if (!IsPrimary || listener is not null) throw new InvalidOperationException("Only the primary viewer may listen once.");
        listener = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    await server!.WaitForConnectionAsync(stop.Token);
                    using var request = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                    request.CancelAfter(TimeSpan.FromSeconds(2));
                    var command = new byte[1];
                    await server.ReadExactlyAsync(command, request.Token);
                    if (command[0] == 1) activate();
                    await server.WriteAsync(new byte[] { command[0] == 1 ? (byte)1 : (byte)0 }, request.Token);
                    // Wait for receipt before disconnecting, so an unread acknowledgment is not discarded.
                    await server.ReadExactlyAsync(new byte[1], request.Token);
                }
                catch (OperationCanceledException) { }
                catch (IOException) { }
                catch (ObjectDisposedException) when (stop.IsCancellationRequested) { }
                finally
                {
                    try { if (server!.IsConnected) server.Disconnect(); }
                    catch (IOException) { }
                    catch (ObjectDisposedException) when (stop.IsCancellationRequested) { }
                }
            }
        });
    }

    public bool ActivateExisting()
    {
        if (IsPrimary) throw new InvalidOperationException("Primary viewer cannot activate itself through IPC.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, TokenImpersonationLevel.Identification);
            client.ConnectAsync(timeout.Token).GetAwaiter().GetResult();
            // Grant only the kernel-identified owner permission to request foreground focus.
            if (GetNamedPipeServerProcessId(client.SafePipeHandle, out var pid)) AllowSetForegroundWindow(pid);
            client.WriteAsync(new byte[] { 1 }, timeout.Token).AsTask().GetAwaiter().GetResult();
            var acknowledgment = new byte[1];
            client.ReadExactlyAsync(acknowledgment, timeout.Token).AsTask().GetAwaiter().GetResult();
            client.WriteAsync(new byte[] { 0 }, timeout.Token).AsTask().GetAwaiter().GetResult();
            return acknowledgment[0] == 1;
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or UnauthorizedAccessException) { return false; }
    }

    // Mutex ownership is acquired and released on Program.Main's same STA thread.
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        stop.Cancel(); server?.Dispose();
        try { listener?.GetAwaiter().GetResult(); }
        finally
        {
            if (IsPrimary) mutex.ReleaseMutex();
            mutex.Dispose(); stop.Dispose();
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint processId);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool AllowSetForegroundWindow(uint processId);
}
