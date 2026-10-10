using System.Collections.Concurrent;
using System.Text.Json;

namespace Grayjay.Desktop.CEF.LinuxCdm;

internal interface IPlaybackGuest : IAsyncDisposable
{
    bool IsRunning { get; }
    Task StartAsync(string accelerator, CancellationToken cancellation);
    Task<JsonElement> RequestAsync(string command, string data, CancellationToken cancellation, Action<JsonElement>? onResponse = null);
}

internal sealed class PlaybackGuestSession : IAsyncDisposable
{
    private readonly SemaphoreSlim lifecycle = new(1, 1);
    private readonly ConcurrentDictionary<string, string> owners = new();
    private readonly CancellationTokenSource stop;
    private readonly Func<CancellationToken, Task<PlayerRuntime>> prepare;
    private readonly Func<PlayerRuntime, Func<JsonElement, Task>, IPlaybackGuest> create;
    private readonly Func<string, JsonElement, Task> callback;
    private readonly string preferredAccelerator;
    private IPlaybackGuest? guest;
    private PlayerRuntime? runtime;
    private string? certificate, token;

    internal PlaybackGuestSession(Func<CancellationToken, Task<PlayerRuntime>> prepare,
        Func<PlayerRuntime, Func<JsonElement, Task>, IPlaybackGuest> create,
        Func<string, JsonElement, Task> callback, CancellationToken cancellation, string? preferredAccelerator = null)
    {
        this.prepare = prepare; this.create = create; this.callback = callback;
        this.preferredAccelerator = preferredAccelerator ?? QemuGuest.PreferredAccelerator;
        stop = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
    }

    internal async Task StartAsync(string playbackToken, string serviceCertificate)
    {
        _ = Convert.FromBase64String(serviceCertificate);
        await lifecycle.WaitAsync(stop.Token);
        try
        {
            await EndPlaybackAsync();
            var prepared = await prepare(stop.Token);
            if (guest is null || !guest.IsRunning || runtime != prepared ||
                (certificate is not null && certificate != serviceCertificate))
            {
                await DisposeGuestAsync();
                await StartWithFallbackAsync(prepared);
            }
            if (certificate is null)
            {
                if (serviceCertificate.Length > 0)
                    await guest!.RequestAsync("CERT", serviceCertificate, stop.Token);
                runtime = prepared; certificate = serviceCertificate;
            }
            token = playbackToken;
        }
        catch { await DisposeGuestAsync(); throw; }
        finally { lifecycle.Release(); }
    }

    internal async Task WarmupAsync()
    {
        await lifecycle.WaitAsync(stop.Token);
        try
        {
            if (guest?.IsRunning == true) return;
            var prepared = await prepare(stop.Token);
            await DisposeGuestAsync();
            await StartWithFallbackAsync(prepared);
            runtime = prepared;
        }
        catch { await DisposeGuestAsync(); throw; }
        finally { lifecycle.Release(); }
    }

    private async Task StartWithFallbackAsync(PlayerRuntime prepared)
    {
        try { await StartGuestAsync(prepared, preferredAccelerator); }
        catch (Exception e) when (preferredAccelerator != "tcg" && !stop.IsCancellationRequested)
        {
            Console.Error.WriteLine("Playback guest startup with " + preferredAccelerator + " failed; retrying with tcg: " + e);
            await DisposeGuestAsync();
            await StartGuestAsync(prepared, "tcg");
        }
    }

    private async Task StartGuestAsync(PlayerRuntime prepared, string accelerator)
    {
        IPlaybackGuest? started = null;
        started = create(prepared, async message =>
        {
            if (!ReferenceEquals(guest, started)) return;
            string? owner;
            if (message.TryGetProperty("session", out var session))
                owners.TryGetValue(session.GetString()!, out owner);
            else owner = token;
            if (owner is not null) await callback(owner, message);
        });
        guest = started;
        await started.StartAsync(accelerator, stop.Token);
    }

    internal async Task<JsonElement> RequestAsync(string playbackToken, string command, string data, string? session = null)
    {
        await lifecycle.WaitAsync(stop.Token);
        try
        {
            if (token != playbackToken || guest is null || !guest.IsRunning)
                throw new InvalidOperationException("Playback session is no longer active.");
            if (session is not null && (!owners.TryGetValue(session, out var owner) || owner != playbackToken))
                throw new InvalidOperationException("Playback license is no longer active.");
            return await guest.RequestAsync(command, data, stop.Token, response =>
            {
                if (command == "SESSION") owners[response.GetProperty("session").GetString()!] = playbackToken;
            });
        }
        finally { lifecycle.Release(); }
    }

    internal async Task CloseAsync(string playbackToken)
    {
        await lifecycle.WaitAsync(stop.Token);
        try
        {
            if (token != playbackToken) return;
            await EndPlaybackAsync();

        }
        finally { lifecycle.Release(); }
    }

    private async Task EndPlaybackAsync()
    {
        token = null;
        var sessions = owners.Keys.ToArray(); owners.Clear();
        if (guest is null) return;
        try
        {
            foreach (string session in sessions) await guest.RequestAsync("CLOSE", session, stop.Token);
        }
        catch { await DisposeGuestAsync(); }
    }

    private async Task DisposeGuestAsync()
    {
        var previous = guest; guest = null; token = null; owners.Clear(); runtime = null; certificate = null;
        if (previous is not null) await previous.DisposeAsync();
    }

    internal async Task StopAsync()
    {
        await lifecycle.WaitAsync();
        try { await DisposeGuestAsync(); }
        finally { lifecycle.Release(); }
    }

    public async ValueTask DisposeAsync() { stop.Cancel(); await StopAsync(); }
}
