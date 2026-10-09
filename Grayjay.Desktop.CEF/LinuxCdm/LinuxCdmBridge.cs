using System.Collections.Concurrent;
using System.Text.Json;
using JustCef;

namespace Grayjay.Desktop.CEF.LinuxCdm;

internal sealed class LinuxCdmBridge : IAsyncDisposable
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly RuntimeAssets assets;
    private readonly string cache;
    private readonly ConcurrentDictionary<int, WindowSession> windows = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> setups = new();
    private readonly CancellationTokenSource stop = new();
    internal LinuxCdmBridge(string cache) { this.cache = cache; assets = new(cache); }
    internal static LinuxCdmBridge? Create(string cache) => RuntimeAssets.Supported ? new(cache) : null;
    private sealed class WindowSession
    {
        internal readonly SemaphoreSlim Lifecycle = new(1, 1);
        internal QemuGuest? Guest;
        internal string? Token;
    }
    private WindowSession ForWindow(JustCefWindow window) => windows.GetOrAdd(window.Identifier, identifier =>
    {
        var session = new WindowSession();
        window.OnClose += () => { _ = CloseWindowAsync(window.Identifier); };
        window.OnFrameLoadStart += info => { if (info.IsMainFrame) _ = CloseWindowAsync(window.Identifier, remove: false); };
        return session;
    });
    internal async Task<string?> HandleAsync(JustCefWindow window, string method, string? json)
    {
        if (!RuntimeAssets.Supported) throw new PlatformNotSupportedException();
        using var document = JsonDocument.Parse(json ?? "null"); var payload = document.RootElement;
        if (method == "linuxCdm.capabilities") return JsonSerializer.Serialize(new { enabled = true, ready = assets.Ready, protocol = RuntimeAssets.ProtocolVersion }, Json);
        string token = payload.GetProperty("token").GetString() ?? throw new ArgumentException("Missing playback token.");
        if (token.Length is < 1 or > 128) throw new ArgumentException("Invalid playback token.");
        if (method == "linuxCdm.prepare")
        {
            _ = ForWindow(window);
            using var cancel = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
            if (!setups.TryAdd(window.Identifier + ":" + token, cancel)) throw new InvalidOperationException("Playback preparation is already running.");
            try
            {
                await assets.EnsureAsync(async progress =>
                {
                    await window.CallBridgeRpcAsync("linuxCdm.progress", JsonSerializer.Serialize(new { token, progress }, Json))
                        .WaitAsync(TimeSpan.FromSeconds(10), cancel.Token);
                }, cancel.Token);
                return "true";
            }
            finally { setups.TryRemove(window.Identifier + ":" + token, out _); }
        }
        if (method == "linuxCdm.cancelSetup")
        { if (setups.TryGetValue(window.Identifier + ":" + token, out var setup)) setup.Cancel(); return "true"; }
        var session = ForWindow(window);
        if (method == "linuxCdm.start")
        {
            await session.Lifecycle.WaitAsync(stop.Token);
            try
            {
                if (session.Guest is not null) await session.Guest.DisposeAsync();
                session.Guest = null; session.Token = token;
                var runtime = await assets.EnsureAsync(_ => Task.CompletedTask, stop.Token);
                var logDirectory = Path.Combine(cache, "logs", window.Identifier + "-" + Guid.NewGuid().ToString("N"));
                bool running = false;
                Func<JsonElement, Task> callback = async message =>
                {
                    if (session.Token == token && (running || message.GetProperty("event").GetString() != "fatal"))
                        await window.CallBridgeRpcAsync("linuxCdm.event", JsonSerializer.Serialize(new { token, @event = message }, Json))
                            .WaitAsync(TimeSpan.FromSeconds(10), stop.Token);
                };
                string accelerator = QemuGuest.PreferredAccelerator;
                var guest = new QemuGuest(runtime, logDirectory, callback);
                session.Guest = guest;
                try { await guest.StartAsync(accelerator, stop.Token); }
                catch when (accelerator != "tcg" && !stop.IsCancellationRequested)
                {
                    await guest.DisposeAsync();
                    guest = new(runtime, logDirectory, callback);
                    session.Guest = guest;
                    await guest.StartAsync("tcg", stop.Token);
                }
                session.Guest = guest;
                running = true;
                return "true";
            }
            catch { session.Token = null; if (session.Guest is not null) await session.Guest.DisposeAsync(); session.Guest = null; throw; }
            finally { session.Lifecycle.Release(); }
        }
        if (method == "linuxCdm.close")
        {
            await session.Lifecycle.WaitAsync(stop.Token);
            try
            {
                if (session.Token == token)
                { session.Token = null; if (session.Guest is not null) await session.Guest.DisposeAsync(); session.Guest = null; }
                return "true";
            }
            finally { session.Lifecycle.Release(); }
        }
        if (session.Token != token || session.Guest is null) throw new InvalidOperationException("Playback session is no longer active.");
        string data = payload.GetProperty("data").GetString() ?? throw new ArgumentException("Missing CDM request data.");
        string command = method switch
        {
            "linuxCdm.session" => "SESSION", "linuxCdm.certificate" => "CERT", "linuxCdm.fragment" => "FRAGMENT",
            "linuxCdm.update" => "UPDATE", _ => throw new ArgumentException("Unknown protected playback method.")
        };
        if (command == "UPDATE")
        {
            string id = payload.GetProperty("session").GetString()!;
            _ = Convert.FromBase64String(id); data = id + " " + data;
        }
        return (await session.Guest.RequestAsync(command, data, stop.Token)).GetRawText();
    }
    private async Task CloseWindowAsync(int id, bool remove = true)
    {
        foreach (var pair in setups) if (pair.Key.StartsWith(id + ":", StringComparison.Ordinal)) pair.Value.Cancel();
        if (!windows.TryGetValue(id, out var session)) return;
        await session.Lifecycle.WaitAsync();
        try
        { session.Token = null; if (session.Guest is not null) await session.Guest.DisposeAsync(); session.Guest = null; if (remove) windows.TryRemove(id, out _); }
        finally { session.Lifecycle.Release(); }
    }
    public async ValueTask DisposeAsync()
    {
        stop.Cancel();
        await Task.WhenAll(windows.Keys.Select(id => CloseWindowAsync(id)));
    }
}
