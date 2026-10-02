using System.Net;
using Grayjay.Desktop.POC;

namespace Grayjay.ClientServer.Casting;

public class CastingDeviceConnectionState
{
    public CastConnectionState State { get; private set; } = CastConnectionState.Disconnected;
    public event Action<CastConnectionState>? StateChanged;
    public void SetState(CastConnectionState state)
    {
        State = state;
        StateChanged?.Invoke(State);
    }
}

public class CastingDevicePlaybackState
{
    public bool IsPlaying { get; private set; }
    public event Action<bool>? IsPlayingChanged;
    public void SetIsPlaying(bool isPlaying)
    {
        if (IsPlaying != isPlaying && _lastTimeChanged != null)
        {
            Time = ExpectedCurrentTime;
            _lastTimeChanged = DateTime.Now;
        }
        IsPlaying = isPlaying;
        IsPlayingChanged?.Invoke(isPlaying);
    }

    public TimeSpan Duration { get; private set; }
    public event Action<TimeSpan>? DurationChanged;
    public void SetDuration(TimeSpan duration)
    {
        Duration = duration;
        DurationChanged?.Invoke(duration);
    }

    public TimeSpan Time { get; private set; }
    public event Action<TimeSpan>? TimeChanged;
    private DateTime? _lastTimeChanged = null;
    public void SetTime(TimeSpan time)
    {
        Time = time;
        TimeChanged?.Invoke(time);
        _lastTimeChanged = DateTime.Now;
    }

    public TimeSpan ExpectedCurrentTime
    {
        get
        {
            if (IsPlaying && _lastTimeChanged != null)
                return Time + TimeSpan.FromTicks((long)((DateTime.Now - _lastTimeChanged.Value).Ticks * (Speed > 0 ? Speed : 1)));
            else
                return Time;
        }
    }

    public double Volume { get; private set; } = 1.0;
    public event Action<double>? VolumeChanged;
    public void SetVolume(double volume)
    {
        Volume = volume;
        VolumeChanged?.Invoke(volume);
    }

    public double Speed { get; private set; }
    public event Action<double>? SpeedChanged;
    public void SetSpeed(double speed)
    {
        if (_lastTimeChanged != null)
        {
            Time = ExpectedCurrentTime;
            _lastTimeChanged = DateTime.Now;
        }
        Speed = speed;
        SpeedChanged?.Invoke(speed);
    }

    public event Action? MediaItemEnded;
    public void MediaItemDidEnd()
    {
        MediaItemEnded?.Invoke();
    }

    public bool IsSame(CastingDevicePlaybackState state)
    {
        return IsPlaying == state.IsPlaying &&
            Duration == state.Duration &&
            Volume == state.Volume &&
            ExpectedCurrentTime == state.ExpectedCurrentTime &&
            Speed == state.Speed &&
            Time == state.Time;
    }
}

public class CastingDevice : IDisposable
{
    public readonly CastingDeviceConnectionState ConnectionState = new();
    public readonly CastingDevicePlaybackState PlaybackState = new();

    protected CastingDevice()
    {
        inner = null!;
        info = null!;
    }

    internal readonly FCast.SenderSDK.CastingDevice inner;
    private CastingDeviceInfo info;

    public bool IsSame(CastingDevice device)
    {
        return DeviceInfo.Equals(device.DeviceInfo) &&
            ConnectionState.State == device.ConnectionState.State &&
            PlaybackState.IsSame(device.PlaybackState) &&
            CanSetVolume == device.CanSetVolume &&
            CanSetSpeed == device.CanSetSpeed &&
            LocalEndPoint == device.LocalEndPoint;
    }

    internal void UpdateInfo(FCast.SenderSDK.DeviceInfo info) {
        inner.SetAddresses(info.Addresses);
        inner.SetPort(info.Port);
        this.info = CastingDeviceInfo.FromRsInfo(info);
    }

    internal class EventHandler: FCast.SenderSDK.DeviceEventHandler {
        private Action<IPAddress> _localEndPointChanged;
        private CastingDeviceConnectionState ConnectionState;
        private Action<FCast.SenderSDK.ReceiverCapabilities?> _capabilitiesChanged;
        private CastingDevicePlaybackState PlaybackState;

        public EventHandler(Action<IPAddress> _localEndPointChanged, Action<FCast.SenderSDK.ReceiverCapabilities?> _capabilitiesChanged, CastingDeviceConnectionState ConnectionState, CastingDevicePlaybackState PlaybackState) {
            this.ConnectionState = ConnectionState;
            this.PlaybackState = PlaybackState;
            this._localEndPointChanged = _localEndPointChanged;
            this._capabilitiesChanged = _capabilitiesChanged;
        }

        public void ConnectionStateChanged(FCast.SenderSDK.DeviceConnectionState state) {
            switch (state) {
            case FCast.SenderSDK.DeviceConnectionState.Connecting:
                ConnectionState.SetState(CastConnectionState.Connecting);
                break;
            case FCast.SenderSDK.DeviceConnectionState.Connected(
                FCast.SenderSDK.IpAddr usedRemoteAddr,
                FCast.SenderSDK.IpAddr localAddr,
                var capabilities
            ):
                _capabilitiesChanged(capabilities);
                _localEndPointChanged(localAddr switch {
                        FCast.SenderSDK.IpAddr.V4(byte @o1, byte @o2, byte @o3, byte @o4) =>
                            new IPAddress([@o1, @o2, @o3, @o4]),
                        FCast.SenderSDK.IpAddr.V6(
                            byte @o1,
                            byte @o2,
                            byte @o3,
                            byte @o4,
                            byte @o5,
                            byte @o6,
                            byte @o7,
                            byte @o8,
                            byte @o9,
                            byte @o10,
                            byte @o11,
                            byte @o12,
                            byte @o13,
                            byte @o14,
                            byte @o15,
                            byte @o16,
                            uint @scopeId
                        ) =>
                            new IPAddress(
                                [@o1, @o2, @o3, @o4, @o5, @o6, @o7, @o8, @o9, @o10, @o11, @o12, @o13, @o14, @o15, @o16],
                                @scopeId
                            ),
                    });
                ConnectionState.SetState(CastConnectionState.Connected);
                break;
            case FCast.SenderSDK.DeviceConnectionState.Reconnecting:
                ConnectionState.SetState(CastConnectionState.Connecting);
                break;
            case FCast.SenderSDK.DeviceConnectionState.Disconnected:
                ConnectionState.SetState(CastConnectionState.Disconnected);
                break;
            }
        }

        public void VolumeChanged(double volume) => PlaybackState.SetVolume(volume);
        public void TimeChanged(double time) => PlaybackState.SetTime(TimeSpan.FromSeconds(time));
        public void PlaybackStateChanged(FCast.SenderSDK.PlaybackState state) {
            PlaybackState.SetIsPlaying(state == FCast.SenderSDK.PlaybackState.Playing);
            if (state == FCast.SenderSDK.PlaybackState.Ended) {
                PlaybackState.MediaItemDidEnd();
            }
        }
        public void DurationChanged(double duration) => PlaybackState.SetDuration(TimeSpan.FromSeconds(duration));
        public void SpeedChanged(double speed) => PlaybackState.SetSpeed(speed);
        public void SourceChanged(FCast.SenderSDK.Source @source) {}
        public void PlaybackStopped() {}
        public void PlaybackError(string message) => Logger.e(nameof(CastingDevice), $"Playback error: {@message}");
        public void TracksAvailable(FCast.SenderSDK.MediaTrack[] tracks) {}
        public void TrackSelected(uint? id, FCast.SenderSDK.MediaTrackType typ) {}
        public void TracksChanged(FCast.SenderSDK.TrackList tracks) {}
        public void QueueChanged(FCast.SenderSDK.QueueState queue) {}
        public void CommandError(FCast.SenderSDK.ReceiverError error) => Logger.e(nameof(CastingDevice), $"Command error: {error}");
    }

    internal CastingDevice(FCast.SenderSDK.CastingDevice dev, CastingDeviceInfo info) {
        inner = dev;
        this.info = info;
    }

    public virtual CastingDeviceInfo DeviceInfo { get => info; set => info = value; }

    public virtual bool CanSetVolume => inner.SupportsFeature(FCast.SenderSDK.DeviceFeature.SetVolume);

    public virtual bool CanSetSpeed => inner.SupportsFeature(FCast.SenderSDK.DeviceFeature.SetSpeed);

    private IPEndPoint? _localEndPoint = null;
    private FCast.SenderSDK.ReceiverCapabilities? _receiverCapabilities = null;
    public virtual bool IsSabrSupported => _receiverCapabilities?.Media?.Protocols?.Contains("sabr") == true;
    public virtual bool SupportsExternalSubtitles => _receiverCapabilities?.Media?.ExternalSubtitles == true;

    public virtual Task<bool> AddSubtitleUrlAsync(string url, string? name)
    {
        try {
            inner.AddSubtitleSource(new FCast.SenderSDK.SubtitleSource(new FCast.SenderSDK.SubtitleContent.Url(url), true, name));
            return Task.FromResult(true);
        } catch (Exception e) {
            Logger.e(nameof(CastingDevice), "Failed to add subtitle URL", e);
            return Task.FromResult(false);
        }
    }

    public virtual Task<bool> AddSubtitleAsync(byte[] data, string contentType, string? name)
    {
        try {
            inner.AddSubtitleSource(new FCast.SenderSDK.SubtitleSource(new FCast.SenderSDK.SubtitleContent.Data(data, contentType), true, name));
            return Task.FromResult(true);
        } catch (Exception e) {
            Logger.e(nameof(CastingDevice), "Failed to add subtitle source", e);
            return Task.FromResult(false);
        }
    }
    public virtual Task<bool> DisableSubtitlesAsync()
    {
        try {
            inner.ChangeTrack(null, FCast.SenderSDK.MediaTrackType.Subtitle);
            return Task.FromResult(true);
        } catch (Exception e) {
            Logger.e(nameof(CastingDevice), "Failed to disable subtitles", e);
            return Task.FromResult(false);
        }
    }

    public virtual IPEndPoint? LocalEndPoint => _localEndPoint;
    public IPAddress MediaAddress => CastingAddress.Select(
        LocalEndPoint?.Address ?? throw new InvalidOperationException("Receiver is not connected."), DeviceInfo.IPAddresses);


    public virtual Task ChangeSpeedAsync(double speed, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try {
            inner.ChangeSpeed(speed);
        } catch (Exception e) {
            Logger.e(nameof(CastingDevice), "Failed to change speed", e);
            throw;
        }
        return Task.CompletedTask;
    }

    public virtual Task ChangeVolumeAsync(double volume, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try {
            inner.ChangeVolume(volume);
            PlaybackState.SetVolume(volume);
        } catch (Exception e) {
            Logger.e(nameof(CastingDevice), "Failed to change volume", e);
            throw;
        }
        return Task.CompletedTask;
    }

    public virtual Task MediaLoadAsync(string streamType, string contentType, string contentId, TimeSpan resumePosition, TimeSpan duration, String? title, String thumbnailUrl, double? speed = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (ConnectionState.State != CastConnectionState.Connected || !inner.IsReady())
            throw new InvalidOperationException("The casting device is not ready to load media.");
        try {
            inner.Load(
                new FCast.SenderSDK.LoadRequest.Video(
                    contentType,
                    contentId,
                    resumePosition.TotalSeconds,
                    speed,
                    null,
                    new FCast.SenderSDK.Metadata(title, thumbnailUrl),
                    null
                ),
                500
            );
        } catch (Exception e) {
            Logger.e(nameof(CastingDevice), "Failed to load media", e);
            throw;
        }
        return Task.CompletedTask;
    }

    public virtual Task MediaPauseAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try {
            inner.PausePlayback();
        } catch (Exception e) {
            Logger.e(nameof(CastingDevice), "Failed to pause playback", e);
            throw;
        }
        return Task.CompletedTask;
    }

    public virtual Task MediaResumeAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try {
            inner.ResumePlayback();
        } catch (Exception e) {
            Logger.e(nameof(CastingDevice), "Failed to resume playback", e);
            throw;
        }
        return Task.CompletedTask;
    }

    public virtual Task MediaSeekAsync(TimeSpan time, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try {
            inner.Seek(time.TotalSeconds);
        } catch (Exception e) {
            Logger.e(nameof(CastingDevice), "Failed to seek", e);
            throw;
        }
        return Task.CompletedTask;
    }

    public virtual Task MediaStopAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try {
            inner.StopPlayback();
        } catch (Exception e) {
            Logger.e(nameof(CastingDevice), "Failed to stop playback", e);
            throw;
        }
        return Task.CompletedTask;
    }

    public virtual void Start()
    {
        try {
            inner.Connect(
                new FCast.SenderSDK.ApplicationInfo("Grayjay Desktop", $"{Constants.App.Version}-{Constants.App.VersionType}",
                    System.Runtime.InteropServices.RuntimeInformation.OSDescription),
                new EventHandler((ip) => _localEndPoint = new IPEndPoint(ip, 0), (caps) => {
                    _receiverCapabilities = caps;
                    Logger.i(nameof(CastingDevice), $"Receiver capabilities: protocols=[{string.Join(", ", caps?.Media?.Protocols ?? [])}], isSabrSupported={IsSabrSupported}");
                }, ConnectionState, PlaybackState),
                1000
            );
        } catch (Exception e) {
            Logger.e(nameof(CastingDevice), "Failed to connect to device", e);
            throw;
        }
    }

    public virtual void Stop()
    {
        try {
            inner.Disconnect();
        } catch (Exception e) {
            Logger.e(nameof(CastingDevice), "Failed to disconnect from device", e);
        }
    }

    private bool _disposed;
    public virtual void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { Stop(); }
        finally { inner?.Dispose(); }
    }

}
