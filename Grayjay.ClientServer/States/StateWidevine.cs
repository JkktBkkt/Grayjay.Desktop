using Grayjay.Engine.Packages;
using JustCef;

using Logger = Grayjay.Desktop.POC.Logger;

namespace Grayjay.ClientServer.States;

public static class StateWidevine
{
    private static readonly object _lock = new object();
    private static readonly SemaphoreSlim _refreshLock = new SemaphoreSlim(1, 1);
    private static WidevineStatus? _status;
    private static bool _cdmLoadedAtStartup;
    private static Func<Task<WidevineStatus>>? _statusRefresher;

    public static WidevineStatus? Status
    {
        get
        {
            lock (_lock)
            {
                return _status;
            }
        }
    }

    public static bool IsPlaybackAvailable
    {
        get
        {
            lock (_lock)
            {
                return IsAvailable(_status);
            }
        }
    }

    /// <summary>
    /// Raised when protected playback goes from unavailable to available. Plugins snapshot
    /// the supported features when their script is evaluated, so those evaluated earlier are stale.
    /// </summary>
    public static event Action? PlaybackBecameAvailable;

    /// <summary>
    /// Marks the CDM as registered at browser startup (Linux hint file), which makes it usable
    /// regardless of the component updater state.
    /// </summary>
    public static void SetCdmLoadedAtStartup(bool loaded)
    {
        bool becameAvailable;
        lock (_lock)
        {
            bool wasAvailable = IsAvailable(_status);
            _cdmLoadedAtStartup = loaded;
            becameAvailable = ApplyAvailability(wasAvailable, _status);
        }
        if (becameAvailable)
            RaisePlaybackBecameAvailable();
    }

    private static bool IsAvailable(WidevineStatus? status)
    {
        return _cdmLoadedAtStartup || status is { Installed: true, RequiresRestart: false };
    }

    public static void SetStatusRefresher(Func<Task<WidevineStatus>>? refresher)
    {
        _statusRefresher = refresher;
    }

    public static async Task RefreshAsync()
    {
        var refresher = _statusRefresher;
        if (refresher == null)
            return;

        await _refreshLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var status = await refresher().ConfigureAwait(false);
            if (status != null)
                Update(status);
        }
        catch (Exception ex)
        {
            Logger.w(nameof(StateWidevine), "On-demand Widevine status refresh failed: " + ex.Message, ex);
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    public static void Update(WidevineStatus status)
    {
        bool becameAvailable;
        lock (_lock)
        {
            bool wasAvailable = IsAvailable(_status);
            _status = status;
            becameAvailable = ApplyAvailability(wasAvailable, status);
        }
        if (becameAvailable)
            RaisePlaybackBecameAvailable();
    }

    // Must be called while holding _lock; returns whether availability went from false to true.
    private static bool ApplyAvailability(bool wasAvailable, WidevineStatus? status)
    {
        bool isAvailable = IsAvailable(status);
        PackageBridge.WidevineSupported = isAvailable;
        return isAvailable && !wasAvailable;
    }

    private static void RaisePlaybackBecameAvailable()
    {
        try
        {
            PlaybackBecameAvailable?.Invoke();
        }
        catch (Exception ex)
        {
            Logger.w(nameof(StateWidevine), "A Widevine availability handler failed: " + ex.Message, ex);
        }
    }
}
