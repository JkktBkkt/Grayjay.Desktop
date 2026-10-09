using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Channels;
using System.IO.Pipes;
using System.Text;
using System.Security.Principal;

namespace Grayjay.Desktop.CEF.LinuxCdm;

internal sealed class QemuGuest : IPlaybackGuest
{
    private readonly PlayerRuntime runtime;
    private readonly string logs;
    private readonly Func<JsonElement, Task> callback;
    private readonly CancellationTokenSource stop = new();
    private readonly SemaphoreSlim writer = new(1, 1);
    private sealed record PendingRequest(TaskCompletionSource<JsonElement> Completion, Action<JsonElement>? OnResponse);
    private readonly ConcurrentDictionary<int, PendingRequest> pending = new();
    private readonly Channel<JsonElement> events = Channel.CreateUnbounded<JsonElement>();
    private readonly TaskCompletionSource<bool> initialized = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<Task> workers = [];
    private Process? process;
    private NamedPipeClientStream? pipe;
    private StreamReader? input;
    private StreamWriter? output;
    private int nextId, disposed, failed;
    private string lastDiagnostic = "";
    internal QemuGuest(PlayerRuntime runtime, string logs, Func<JsonElement, Task> callback)
    { this.runtime = runtime; this.logs = logs; this.callback = callback; }

    public bool IsRunning => Volatile.Read(ref failed) == 0 && Volatile.Read(ref disposed) == 0 &&
        initialized.Task.IsCompletedSuccessfully && initialized.Task.Result;

    public async Task StartAsync(string accelerator, CancellationToken cancellation)
    {
        if (!RuntimeAssets.Supported) throw new PlatformNotSupportedException();
        Directory.CreateDirectory(logs);
        var start = new ProcessStartInfo(runtime.Executable)
        {
            WorkingDirectory = runtime.Directory, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        string console = FilePath(Path.Combine(logs, "console.log")).Replace(",", ",,");
        string pipeName = "grayjay-cdm-" + Guid.NewGuid().ToString("N");
        foreach (string argument in new[] {
            "-nodefaults", "-no-user-config", "-no-reboot", "-machine", accelerator == "whpx" ? "q35,smm=off" : "q35",
            // WHPX's in-kernel interrupt controller intermittently drops the virtio-serial interrupt, so the guest stops receiving commands
            "-accel", accelerator == "tcg" ? "tcg,thread=multi,tb-size=16" : accelerator == "whpx" ? "whpx,kernel-irqchip=off" : accelerator,
            "-cpu", accelerator == "hvf" ? "host" : "max", "-smp", "1", "-m", "256",
            "-kernel", FilePath(runtime.Kernel), "-initrd", FilePath(runtime.Initramfs),
            "-append", "console=ttyS0 rdinit=/init panic=-1",
            "-display", "none", "-monitor", "none", "-nic", "none",
            "-L", "data",
            "-chardev", "file,id=boot,path=" + console, "-device", "isa-serial,chardev=boot",
            "-device", "virtio-serial-pci", "-chardev", OperatingSystem.IsWindows() ? "pipe,id=cdm,path=" + pipeName : "stdio,id=cdm,signal=off",
            "-device", "virtserialport,chardev=cdm,nr=1,name=grayjay.cdm" }) start.ArgumentList.Add(argument);
        process = Process.Start(start) ?? throw new IOException("Could not start protected playback.");
        workers.Add(DiagnosticsAsync());
        if (OperatingSystem.IsWindows())
        {

            pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut,
                PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
            using var connection = CancellationTokenSource.CreateLinkedTokenSource(cancellation, stop.Token);
            await pipe.ConnectAsync(10000, connection.Token);
            input = new StreamReader(pipe, new UTF8Encoding(false), false, 64 * 1024, leaveOpen: true);
            output = new StreamWriter(pipe, new UTF8Encoding(false), 64 * 1024, leaveOpen: true) { NewLine = "\n" };
        }
        else { input = process.StandardOutput; output = process.StandardInput; }
        workers.Add(ReadAsync()); workers.Add(DispatchAsync());
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, stop.Token);
        if (!await initialized.Task.WaitAsync(TimeSpan.FromSeconds(30), linked.Token))
            throw new InvalidOperationException("Widevine initialization failed.");
        workers.Add(HeartbeatAsync());
    }
    private async Task ReadAsync()
    {
        Exception failure;
        try
        {
            while (await input!.ReadLineAsync(stop.Token) is { } line)
            {
                if (line.Length > 64 * 1024 * 1024) throw new InvalidDataException("Playback response is too large.");
                using var document = JsonDocument.Parse(line);
                var message = document.RootElement.Clone(); string kind = message.GetProperty("event").GetString()!;
                if (kind == "initialized")
                {
                    if (message.GetProperty("protocol").GetInt32() != RuntimeAssets.ProtocolVersion)
                        throw new InvalidDataException("Playback helper protocol mismatch.");
                    initialized.TrySetResult(message.GetProperty("success").GetBoolean());
                }
                else if (kind is "created" or "resolved" or "rejected" or "fragment" or "error")
                {
                    if (pending.TryRemove(message.GetProperty("id").GetInt32(), out var request))
                    {
                        if (kind is "rejected" or "error") request.Completion.TrySetException(new InvalidOperationException(message.GetProperty("message").GetString()));
                        else
                        {
                            try { request.OnResponse?.Invoke(message); request.Completion.TrySetResult(message); }
                            catch (Exception e) { request.Completion.TrySetException(e); }
                        }
                    }
                }
                else await events.Writer.WriteAsync(message, stop.Token);
            }
            failure = new IOException("Protected playback stopped unexpectedly." + (string.IsNullOrEmpty(lastDiagnostic) ? "" : " " + lastDiagnostic));
        }
        catch (Exception e) { failure = e; }
        Interlocked.Exchange(ref failed, 1);
        initialized.TrySetException(failure);
        foreach (var request in pending.Values) request.Completion.TrySetException(failure);
        if (!stop.IsCancellationRequested)
        {
            await events.Writer.WriteAsync(JsonSerializer.SerializeToElement(new { @event = "fatal", message = failure.Message }));
        }
        events.Writer.TryComplete();
    }
    private async Task DispatchAsync()
    {
        try { await foreach (var message in events.Reader.ReadAllAsync(stop.Token)) await callback(message); }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception e) { Console.Error.WriteLine("Protected playback callback failed: " + e.Message); }
    }
    private async Task DiagnosticsAsync()
    {
        try
        {
            await using var log = new StreamWriter(Path.Combine(logs, "runtime.log"), append: true);
            while (await process!.StandardError.ReadLineAsync(stop.Token) is { } line)
            { lastDiagnostic = line.Length <= 500 ? line : line[..500]; await log.WriteLineAsync(line); }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
    }
    private async Task HeartbeatAsync()
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
            while (await timer.WaitForNextTickAsync(stop.Token)) await SendAsync("PING", stop.Token);
        }
        catch (Exception e) when (stop.IsCancellationRequested || e is IOException or ObjectDisposedException) { }
    }
    public async Task<JsonElement> RequestAsync(string command, string data, CancellationToken cancellation, Action<JsonElement>? onResponse = null)
    {
        if (!IsRunning) throw new IOException("Protected playback guest is not running.");
        if (data.Length > 48 * 1024 * 1024 || data.Contains('\n') || data.Contains('\r'))
            throw new InvalidDataException("Invalid playback request.");
        int id = Interlocked.Increment(ref nextId);
        var request = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[id] = new(request, onResponse);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, stop.Token);
        linked.CancelAfter(TimeSpan.FromSeconds(45));
        try
        {
            await SendAsync(command + " " + id + " " + data, linked.Token);
            return await request.Task.WaitAsync(linked.Token);
        }
        finally { pending.TryRemove(id, out _); }
    }
    private async Task SendAsync(string line, CancellationToken cancellation)
    {
        await writer.WaitAsync(cancellation);
        try { await output!.WriteLineAsync(line.AsMemory(), cancellation); await output.FlushAsync(cancellation); }
        finally { writer.Release(); }
    }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        try
        {
            if (process is { HasExited: false })
            {
                try
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                    if (output is not null) await SendAsync("QUIT", timeout.Token);
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));
                }
                catch (Exception e) when (e is IOException or TimeoutException or OperationCanceledException or InvalidOperationException)
                { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            }
        }
        finally
        {
            stop.Cancel();
            pipe?.Dispose();
            try { await Task.WhenAll(workers).WaitAsync(TimeSpan.FromSeconds(3)); }
            catch (Exception e) when (e is OperationCanceledException or TimeoutException or IOException) { }
            process?.Dispose();
            if (pipe is not null)
            {
                input?.Dispose();
                DisposePipeWriter(output);
            }

        }
    }
    internal static void DisposePipeWriter(StreamWriter? stream)
    {
        try { stream?.Dispose(); }
        catch (Exception e) when (e is IOException or ObjectDisposedException) { }
    }
    private static string FilePath(string path) => OperatingSystem.IsWindows() ? WindowsFilePath(Path.GetFullPath(path)) : path;
    internal static string WindowsFilePath(string path)
    {
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal)) return path;
        if (path.StartsWith(@"\\", StringComparison.Ordinal)) return @"\\?\UNC\" + path[2..];
        return @"\\?\" + path;
    }
    internal static string PreferredAccelerator => OperatingSystem.IsWindows() && RuntimeInformation.ProcessArchitecture == Architecture.X64
        ? "whpx" : OperatingSystem.IsMacOS() && RuntimeInformation.ProcessArchitecture == Architecture.X64 ? "hvf" : "tcg";
}
