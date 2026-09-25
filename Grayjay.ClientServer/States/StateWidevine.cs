using Grayjay.Engine.Packages;
using JustCef;

using Logger = Grayjay.Desktop.POC.Logger;

namespace Grayjay.ClientServer.States;

public static class StateWidevine
{
    private static readonly object _lock = new object();
    private static readonly SemaphoreSlim _refreshLock = new SemaphoreSlim(1, 1);
    private static WidevineStatus? _status;
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
                return _status is { Installed: true, RequiresRestart: false };
            }
        }
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
        lock (_lock)
        {
            _status = status;
            PackageBridge.WidevineSupported = status is { Installed: true, RequiresRestart: false };
        }
    }
}
