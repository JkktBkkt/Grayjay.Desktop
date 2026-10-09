using System.Text.Json;
using System.IO.Pipes;
using Grayjay.Desktop.CEF.LinuxCdm;

internal static class PlaybackTests
{
    internal static async Task RunAsync()
    {
        string longBootPath = @"C:\Users\Bank\Downloads\artifacts (4)\Grayjay.Desktop.CEF\bin\Release\net8.0\win-x64\Grayjay.Desktop-win-x64-v19\playback-components\boot\"
            + new string('a', 64) + "-" + new string('b', 64) + ".cpio.gz";
        Check(longBootPath.Length > 260 && QemuGuest.WindowsFilePath(longBootPath) == @"\\?\" + longBootPath,
            "long Windows boot image paths use the extended path namespace");
        string extended = @"\\?\C:\guest\boot.cpio.gz";
        Check(QemuGuest.WindowsFilePath(extended) == extended, "extended Windows paths are preserved");
        Check(QemuGuest.WindowsFilePath(@"\\server\share\guest\boot.cpio.gz") == @"\\?\UNC\server\share\guest\boot.cpio.gz",
            "Windows network paths use the extended UNC namespace");
        string pipeName = "gj-" + Guid.NewGuid().ToString("N")[..8];
        using (var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous))
        using (var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous))
        {
            var connection = server.WaitForConnectionAsync();
            await client.ConnectAsync(2000); await connection;
            var output = new StreamWriter(client, leaveOpen: true);
            output.Write("buffered command");
            client.Dispose();
            QemuGuest.DisposePipeWriter(output);
            Check(true, "disposing buffered output tolerates an already closed named pipe");
        }
        var guests = new List<FakeGuest>();
        var events = new List<(string Token, string Event)>();
        var runtime = new PlayerRuntime("runtime", "qemu", "kernel", "image");
        var fallbackGuests = new List<FakeGuest>();
        await using (var fallback = new PlaybackGuestSession(_ => Task.FromResult(runtime),
            (_, callback) => { var guest = new FakeGuest(callback) { FailStart = fallbackGuests.Count == 0 }; fallbackGuests.Add(guest); return guest; },
            (_, _) => Task.CompletedTask, default, "whpx"))
        {
            await fallback.StartAsync("fallback", "");
            Check(fallbackGuests.Count == 2 && fallbackGuests[0].Disposed &&
                fallbackGuests[0].Accelerator == "whpx" && fallbackGuests[1].Accelerator == "tcg" && fallbackGuests[1].IsRunning,
                "unavailable WHPX is disposed and retried with software emulation");
        }
        var preparation = new TaskCompletionSource<PlayerRuntime>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var session = new PlaybackGuestSession(_ => preparation.Task,
            (_, callback) => { var guest = new FakeGuest(callback); guests.Add(guest); return guest; },
            (token, message) => { events.Add((token, message.GetProperty("event").GetString()!)); return Task.CompletedTask; },
            default);
        var start = session.StartAsync("one", "");
        Check(!start.IsCompleted, "playback waits for component preparation");
        preparation.SetResult(runtime); await start;
        var created = await session.RequestAsync("one", "SESSION", "pssh");
        string id = created.GetProperty("session").GetString()!;
        Check(events.SequenceEqual(new[] { ("one", "message") }), "license events belong to their session before creation returns");
        await session.CloseAsync("one");
        Check(guests[0].Closed.Contains(id) && !guests[0].Disposed, "closing playback closes licenses and retains the guest");
        await session.StartAsync("two", "");
        await session.CloseAsync("one");
        Check(guests.Count == 1 && !guests[0].Disposed, "new playback reuses the guest and ignores stale close requests");
        await Reject(() => session.RequestAsync("one", "SESSION", "pssh"), "stale playback cannot create licenses");
        await Reject(() => session.RequestAsync("two", "UPDATE", id + " response", id), "new playback cannot update an old license");
        await guests[0].EmitAsync("message", id);
        Check(events.Count == 1, "late events from a closed license are discarded");
        var second = await session.RequestAsync("two", "SESSION", "pssh");
        string secondId = second.GetProperty("session").GetString()!;
        await session.StartAsync("three", "Y2VydA==");
        Check(guests.Count == 2 && guests[0].Disposed && guests[0].Closed.Contains(secondId) && guests[1].Certificate == "Y2VydA==",
            "certificate changes close licenses and replace the guest");
        await session.StartAsync("four", "");
        Check(guests.Count == 3 && guests[1].Disposed && guests[2].Certificate is null,
            "a source without a certificate does not inherit one");
        runtime = runtime with { Initramfs = "updated-image" };
        preparation = new(TaskCreationOptions.RunContinuationsAsynchronously); preparation.SetResult(runtime);
        await session.StartAsync("five", "");
        Check(guests.Count == 4 && guests[2].Disposed, "updated boot images replace cached guests");
        guests[3].Running = false;
        await session.StartAsync("six", "");
        Check(guests.Count == 5 && guests[3].Disposed, "failed guests are replaced on the next playback");
        guests[4].FailClose = true;
        await session.RequestAsync("six", "SESSION", "pssh");
        await session.CloseAsync("six");
        Check(guests[4].Disposed, "a failed license close discards the guest");
        await session.StartAsync("seven", "");
        await session.CloseAsync("seven");
        Check(!guests[5].Disposed, "idle guests stay loaded until the window closes");
        await session.StartAsync("eight", "");
        await session.StopAsync();
        Check(guests[5].Disposed, "window reload destroys the guest immediately");
        await session.StartAsync("nine", "");
        await session.DisposeAsync();
        Check(guests[6].Disposed, "window disposal destroys the guest immediately");
    }
    private static void Check(bool condition, string name)
    { if (!condition) throw new Exception(name); Console.WriteLine("PASS " + name); }
    private static async Task Reject(Func<Task> action, string name)
    {
        try { await action(); }
        catch (InvalidOperationException) { Console.WriteLine("PASS " + name); return; }
        throw new Exception(name);
    }
    private sealed class FakeGuest(Func<JsonElement, Task> callback) : IPlaybackGuest
    {
        internal bool Running, Disposed, FailClose, FailStart;
        internal string? Certificate, Accelerator;
        internal readonly List<string> Closed = [];
        internal readonly TaskCompletionSource<bool> Disposal = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int sequence;
        public bool IsRunning => Running && !Disposed;
        public Task StartAsync(string accelerator, CancellationToken cancellation)
        {
            Accelerator = accelerator;
            if (FailStart) throw new IOException("WHPX: No accelerator found");
            Running = true; return Task.CompletedTask;
        }
        public async Task<JsonElement> RequestAsync(string command, string data, CancellationToken cancellation, Action<JsonElement>? onResponse = null)
        {
            if (command == "CLOSE")
            {
                if (FailClose) throw new IOException("Guest stopped");
                Closed.Add(data); await EmitAsync("closed", data);
            }
            if (command == "CERT") Certificate = data;
            var response = JsonSerializer.SerializeToElement(new { @event = command == "SESSION" ? "created" : "resolved", session = "session-" + ++sequence });
            onResponse?.Invoke(response);
            if (command == "SESSION") await EmitAsync("message", response.GetProperty("session").GetString()!);
            return response;
        }
        internal Task EmitAsync(string kind, string session) => callback(JsonSerializer.SerializeToElement(new { @event = kind, session }));
        public ValueTask DisposeAsync() { Disposed = true; Disposal.TrySetResult(true); return ValueTask.CompletedTask; }
    }
}
