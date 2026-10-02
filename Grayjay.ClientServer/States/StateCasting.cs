using System.Net;
using Grayjay.ClientServer.Casting;
using Grayjay.ClientServer.Store;
using Grayjay.ClientServer.Sabr.Cast;

namespace Grayjay.ClientServer.States;

using Logger = Desktop.POC.Logger;

public class StateCasting : IDisposable
{
    private readonly FCast.SenderSDK.CastContext _context = new();
    private readonly HashSet<CastingDevice> _ownedDevices = new();
    private bool _started;
    private bool _disposed;
    private static readonly Lazy<bool> SdkLogging = new(() =>
    {
        FCast.SenderSDK.FcastSenderSdkMethods.InitCustomLogger(new CastLogger());
        return true;
    });
    protected readonly object _castingDeviceLock = new object();
    protected readonly Dictionary<string, CastingDevice> _castingDevices = new Dictionary<string, CastingDevice>();

    //TODO: Add index for id ?
    protected readonly ManagedStore<CastingDeviceInfo> _pinnedDevices = new ManagedStore<CastingDeviceInfo>("pinnedDevices")
        .WithUnique(v => v.Id)
        .WithBackup();

    public List<CastingDeviceInfo> PinnedDevices => _pinnedDevices.GetObjects()
        .Where(dev => dev.Type is CastProtocolType.Chromecast or CastProtocolType.FCast)
        .ToList();
    public List<CastingDevice> DiscoveredDevices
    {
        get
        {
            lock (_castingDeviceLock)
            {
                return _castingDevices.Values.ToList();
            }
        }
    }

    protected CastingDevice? _activeDevice;
    public CastingDevice? ActiveDevice
    {
        get
        {
            lock (_castingDeviceLock)
            {
                return _activeDevice;
            }
        }
    }

    public virtual event Action<CastingDevice?>? ActiveDeviceChanged;
    public event Action<bool>? IsPlayingChanged;
    public event Action<TimeSpan>? DurationChanged;
    public event Action<TimeSpan>? TimeChanged;
    public event Action<double>? VolumeChanged;
    public event Action<double>? SpeedChanged;
    public event Action<CastConnectionState>? StateChanged;
    protected readonly Debouncer _broadcastDevicesDebouncer;
    private Action<CastConnectionState>? _activeStateHandler;

    private List<CastingDeviceInfo> _lastUpdate = new List<CastingDeviceInfo>();

    public StateCasting()
    {
        try
        {
            _pinnedDevices.Load();
        }
        catch (Exception e)
        {
            Logger.i(nameof(StateCasting), $"Failed to load pinned devices '{e.Message}': {e.StackTrace}");
        }

        _broadcastDevicesDebouncer = new Debouncer(TimeSpan.FromSeconds(1), BroadcastDiscoveredDevices);

        GrayjayServer.Instance.WebSocket.OnNewClient += (c) =>
        {
            BroadcastDiscoveredDevices(true);
        };
    }

    private async void BroadcastDiscoveredDevices() => BroadcastDiscoveredDevices(false);
    private async void BroadcastDiscoveredDevices(bool force = false)
    {
        try
        {
            lock (_castingDeviceLock)
                if (_disposed) return;
            var current = DiscoveredDevices.Select(v => v.DeviceInfo).ToList();
            if (force || HasUpdatedChanged(current))
            {
                _lastUpdate = current;
                await GrayjayServer.Instance.WebSocket.Broadcast(current, "discoveredDevicesUpdated");
            }
        }
        catch (Exception e)
        {
            Logger.i(nameof(StateCasting), $"Broadcast discovered devices failed '{e.Message}': {e.StackTrace}");
        }
    }
    private bool HasUpdatedChanged(List<CastingDeviceInfo> current)
        => current.Count != _lastUpdate.Count || current.Any(info => !Equals(_lastUpdate.FirstOrDefault(previous => previous.Id == info.Id), info));





    public void AddPinnedDevice(CastingDeviceInfo castingDeviceInfo)
    {
        _pinnedDevices.Save(castingDeviceInfo);
    }

    public void RemovePinnedDevice(CastingDeviceInfo castingDeviceInfo)
    {
        _pinnedDevices.Delete(castingDeviceInfo);
    }



    protected void BindEvents(CastingDevice castingDevice)
    {
        castingDevice.PlaybackState.IsPlayingChanged += HandleIsPlayingChanged;
        castingDevice.PlaybackState.DurationChanged += HandleDurationChanged;
        castingDevice.PlaybackState.TimeChanged += HandleTimeChanged;
        castingDevice.PlaybackState.VolumeChanged += HandleVolumeChanged;
        castingDevice.PlaybackState.SpeedChanged += HandleSpeedChanged;
        castingDevice.PlaybackState.MediaItemEnded += HandleMediaItemEnded;
        _activeStateHandler = state => HandleStateChanged(castingDevice, state);
        castingDevice.ConnectionState.StateChanged += _activeStateHandler;
    }

    protected void UnbindEvents(CastingDevice castingDevice)
    {
        castingDevice.PlaybackState.IsPlayingChanged -= HandleIsPlayingChanged;
        castingDevice.PlaybackState.DurationChanged -= HandleDurationChanged;
        castingDevice.PlaybackState.TimeChanged -= HandleTimeChanged;
        castingDevice.PlaybackState.VolumeChanged -= HandleVolumeChanged;
        castingDevice.PlaybackState.SpeedChanged -= HandleSpeedChanged;
        castingDevice.PlaybackState.MediaItemEnded -= HandleMediaItemEnded;
        castingDevice.ConnectionState.StateChanged -= _activeStateHandler;
        _activeStateHandler = null;
    }

    private async void HandleIsPlayingChanged(bool isPlaying)
    {
        IsPlayingChanged?.Invoke(isPlaying);

        try
        {
            await GrayjayServer.Instance.WebSocket.Broadcast(isPlaying, "activeDeviceIsPlayingChanged");
        }
        catch (Exception e)
        {
            Logger.e(nameof(StateCasting), "Failed to notify active device IsPlayingChanged.", e);
        }
    }

    private async void HandleDurationChanged(TimeSpan duration)
    {
        DurationChanged?.Invoke(duration);

        try
        {
            await GrayjayServer.Instance.WebSocket.Broadcast(duration.TotalSeconds, "activeDeviceDurationChanged");
        }
        catch (Exception e)
        {
            Logger.e(nameof(StateCasting), "Failed to notify active device DurationChanged.", e);
        }
    }

    private async void HandleTimeChanged(TimeSpan time)
    {
        TimeChanged?.Invoke(time);

        try
        {
            await GrayjayServer.Instance.WebSocket.Broadcast(time.TotalSeconds, "activeDeviceTimeChanged");
        }
        catch (Exception e)
        {
            Logger.e(nameof(StateCasting), "Failed to notify active device TimeChanged.", e);
        }
    }

    private async void HandleVolumeChanged(double volume)
    {
        VolumeChanged?.Invoke(volume);

        try
        {
            await GrayjayServer.Instance.WebSocket.Broadcast(volume, "activeDeviceVolumeChanged");
        }
        catch (Exception e)
        {
            Logger.e(nameof(StateCasting), "Failed to notify active device VolumeChanged.", e);
        }
    }

    private async void HandleSpeedChanged(double speed)
    {
        SpeedChanged?.Invoke(speed);

        try
        {
            await GrayjayServer.Instance.WebSocket.Broadcast(speed, "activeDeviceSpeedChanged");
        }
        catch (Exception e)
        {
            Logger.e(nameof(StateCasting), "Failed to notify active device SpeedChanged.", e);
        }
    }

    private async void HandleMediaItemEnded()
    {
        try
        {
            await GrayjayServer.Instance.WebSocket.Broadcast(null, "activeDeviceMediaItemEnded");
        }
        catch (Exception e)
        {
            Logger.e(nameof(StateCasting), "Failed to notify active device MediaItemEnded.", e);
        }
    }

    private async void HandleStateChanged(CastingDevice device, CastConnectionState state)
    {
        lock (_castingDeviceLock)
        {
            if (_activeDevice != device || _disposed) return;
            if (state == CastConnectionState.Disconnected)
                Disconnect();
        }
        StateChanged?.Invoke(state);

        try
        {
            await GrayjayServer.Instance.WebSocket.Broadcast(state, "activeDeviceStateChanged");
        }
        catch (Exception e)
        {
            Logger.e(nameof(StateCasting), "Failed to notify active device StateChanged.", e);
        }
    }



    private static object _lockObject = new object();
    private static StateCasting? _instance = null;
    public static StateCasting Instance
    {
        get
        {
            lock (_lockObject)
            {
                _instance ??= new StateCasting();
                return _instance;
            }
        }
    }

    public void Connect(CastingDevice castingDevice)
    {
        if (ActiveDevice == castingDevice)
            return;

        try
        {
            _ = _pinnedDevices.SaveAsync(castingDevice.DeviceInfo);
        }
        catch (Exception e)
        {
            Logger.w(nameof(StateCasting), "Failed to save pinned device.", e);
        }

        lock (_castingDeviceLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var oldActiveDevice = ActiveDevice;
            if (oldActiveDevice != null)
                UnbindEvents(oldActiveDevice);

            BindEvents(castingDevice);
            _ownedDevices.Add(castingDevice);
            _activeDevice = castingDevice;
            oldActiveDevice?.Stop();
            try { castingDevice.Start(); }
            catch
            {
                Disconnect();
                throw;
            }
            if (_activeDevice != castingDevice) return;
        }

        ActiveDeviceChanged?.Invoke(castingDevice);

        Task.Run(async () =>
        {
            try
            {
                await GrayjayServer.Instance.WebSocket.Broadcast(castingDevice.DeviceInfo, "activeDeviceChanged");
            }
            catch (Exception e)
            {
                Logger.e(nameof(StateCasting), "Failed to notify active device changed.", e);
            }
        });
    }

    public void Disconnect()
    {
        lock (_castingDeviceLock)
        {
            var oldActiveDevice = ActiveDevice;
            if (oldActiveDevice != null)
                UnbindEvents(oldActiveDevice);

            _activeDevice = null;
            oldActiveDevice?.Stop();
        }

        UmpCasting.Stop();
        ActiveDeviceChanged?.Invoke(null);

        Task.Run(async () =>
        {
            try
            {
                await GrayjayServer.Instance.WebSocket.Broadcast(null, "activeDeviceChanged");
            }
            catch (Exception e)
            {
                Logger.e(nameof(StateCasting), "Failed to notify active device changed.", e);
            }
        });
    }

    private String FormatDeviceInfo(FCast.SenderSDK.DeviceInfo devInfo) {
        return $"{{ name = {devInfo.Name}, protocol = {devInfo.Protocol}, addresses = [{String.Join(", ", devInfo.Addresses.Select(addr => FCast.SenderSDK.FcastSenderSdkMethods.UrlFormatIpAddr(addr)))}], port = {devInfo.Port} }}";
    }

    public void Start()
    {
        lock (_castingDeviceLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_started) return;
            if (!Grayjay.ClientServer.Settings.GrayjaySettings.Instance.Casting.Enabled) return;
            _ = SdkLogging.Value;
            var handler = new DiscoveryEventHandler();
            handler.OnAvailable += UpdateDiscoveredDevice;
            handler.OnChanged += UpdateDiscoveredDevice;
            handler.OnRemoved += name =>
            {
                lock (_castingDeviceLock)
                {
                    if (_disposed) return;
                    _castingDevices.Remove(name);
                }
                _broadcastDevicesDebouncer.Call();
            };
            _context.StartDiscovery(handler);
            _started = true;
        }
    }

    private void UpdateDiscoveredDevice(FCast.SenderSDK.DeviceInfo info)
    {
        lock (_castingDeviceLock)
        {
            if (_disposed) return;
            Logger.d(nameof(StateCasting), $"Device discovered: {FormatDeviceInfo(info)}");
            if (_castingDevices.TryGetValue(info.Name, out var existing))
                existing.UpdateInfo(info);
            else
            {
                var device = new CastingDevice(_context.CreateDeviceFromInfo(info), CastingDeviceInfo.FromRsInfo(info));
                _castingDevices[info.Name] = device;
                _ownedDevices.Add(device);
            }
        }
        _broadcastDevicesDebouncer.Call();
    }

    public void Dispose()
    {
        CastingDevice[] devices;
        lock (_castingDeviceLock)
        {
            if (_disposed) return;
            _disposed = true;
            if (_activeDevice != null) UnbindEvents(_activeDevice);
            _activeDevice = null;
            devices = _ownedDevices.ToArray();
            _ownedDevices.Clear();
            _castingDevices.Clear();
        }
        try
        {
            foreach (var device in devices) device.Dispose();
        }
        finally { _context.Dispose(); }
    }

    private FCast.SenderSDK.IpAddr IPAddressToRsIpAddr(IPAddress a) {
        byte[] bytes = a.GetAddressBytes();
        if (bytes.Length == 4)
        {
            return new FCast.SenderSDK.IpAddr.V4(bytes[0], bytes[1], bytes[2], bytes[3]);
        }
        else if (bytes.Length == 16)
        {
            return new FCast.SenderSDK.IpAddr.V6(bytes[0], bytes[1], bytes[2], bytes[3], bytes[4], bytes[5], bytes[6], bytes[7], bytes[8], bytes[9], bytes[10], bytes[11], bytes[12], bytes[13], bytes[14], bytes[15], (uint)a.ScopeId);
        }
        else
        {
            throw new Exception($"Ip address of length {bytes.Length} is invalid");
        }
    }

    public CastingDevice CreateDevice(CastingDeviceInfo info) {
        FCast.SenderSDK.ProtocolType protoType = info.Type switch
        {
            CastProtocolType.Chromecast => FCast.SenderSDK.ProtocolType.Chromecast,
            CastProtocolType.FCast => FCast.SenderSDK.ProtocolType.FCast,
            _ => throw new Exception($"Invalid cast protocol type {info.Type}")
        };

        FCast.SenderSDK.DeviceInfo rsDeviceInfo = new FCast.SenderSDK.DeviceInfo(
            info.Name,
            protoType,
            info.IPAddresses.Select(a => IPAddressToRsIpAddr(a)).ToArray(),
            checked((ushort)info.Port),
            info.TxtRecords ?? new Dictionary<string, string>()
        );

        lock (_castingDeviceLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var device = new CastingDevice(_context.CreateDeviceFromInfo(rsDeviceInfo), info);
            _ownedDevices.Add(device);
            return device;
        }
    }



}

class DiscoveryEventHandler : FCast.SenderSDK.DeviceDiscovererEventHandler
{
    public event Action<FCast.SenderSDK.DeviceInfo>? OnAvailable;
    public event Action<FCast.SenderSDK.DeviceInfo>? OnChanged;
    public event Action<string>? OnRemoved;

    public void DeviceAvailable(FCast.SenderSDK.DeviceInfo deviceInfo) => OnAvailable?.Invoke(deviceInfo);

    public void DeviceChanged(FCast.SenderSDK.DeviceInfo deviceInfo) => OnChanged?.Invoke(deviceInfo);

    public void DeviceRemoved(string deviceName) => OnRemoved?.Invoke(deviceName);
}

class CastLogger: FCast.SenderSDK.LogHandler {
    public void Log(FCast.SenderSDK.LogLevel level, String tag, String message) {
        Logger.l(
            level switch {
                FCast.SenderSDK.LogLevel.Error => Desktop.POC.LogLevel.Error,
                FCast.SenderSDK.LogLevel.Warn => Desktop.POC.LogLevel.Warning,
                FCast.SenderSDK.LogLevel.Info => Desktop.POC.LogLevel.Info,
                FCast.SenderSDK.LogLevel.Debug => Desktop.POC.LogLevel.Verbose,
                _ => Desktop.POC.LogLevel.Debug,
            },
            tag,
            message
        );
    }
}
