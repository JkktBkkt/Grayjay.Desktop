using System.Reflection;
using System.Text.Json;
using Grayjay.ClientServer;
using Grayjay.ClientServer.Casting;
using Grayjay.ClientServer.Settings;
using Grayjay.ClientServer.States;

namespace Grayjay.Desktop.Tests;

[TestClass]
[DoNotParallelize]
public class CastingSdkTests
{
    [DataTestMethod]
    [DataRow(CastProtocolType.Chromecast)]
    [DataRow(CastProtocolType.FCast)]
    public void SavedDevicesAlwaysUseSdk(CastProtocolType protocol)
    {
        var previousServer = GrayjayServer.Instance;
        _ = new GrayjayServer();
        try
        {
            using var state = new StateCasting();
            var info = new CastingDeviceInfo { Id = "saved-id", Name = "saved-name", Type = protocol,
                Addresses = new() { "127.0.0.1", "::1" }, Port = 46899 };
            var device = state.CreateDevice(info);
            Assert.AreEqual(typeof(CastingDevice), device.GetType());
            Assert.AreSame(info, device.DeviceInfo);
            var inner = (FCast.SenderSDK.CastingDevice)typeof(CastingDevice).GetField("inner", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(device)!;
            Assert.AreEqual(protocol == CastProtocolType.FCast ? FCast.SenderSDK.ProtocolType.FCast : FCast.SenderSDK.ProtocolType.Chromecast, inner.CastingProtocol());
            Assert.AreEqual(2, inner.GetAddresses().Length);
            Assert.AreEqual((ushort)46899, inner.GetPort());
            state.Dispose();
            state.Dispose();
            Assert.ThrowsException<ObjectDisposedException>(() => state.CreateDevice(info));
        }
        finally { typeof(GrayjayServer).GetProperty(nameof(GrayjayServer.Instance))!.SetValue(null, previousServer); }
    }

    [TestMethod]
    public void OldBackendSettingDoesNotDisableCastingOrExposeToggle()
    {
        var settings = JsonSerializer.Deserialize<GrayjaySettings>("{\"Casting\":{\"Enabled\":true,\"Experimental\":false}}")!;
        Assert.IsTrue(settings.Casting.Enabled);
        Assert.IsNull(typeof(GrayjaySettings.CastingSettings).GetProperty("Experimental"));
    }

    [TestMethod]
    public async Task SdkDiscoveryStartsOnceAndStopsOnDisposal()
    {
        var previousServer = GrayjayServer.Instance;
        var wasEnabled = GrayjaySettings.Instance.Casting.Enabled;
        _ = new GrayjayServer();
        try
        {
            using var state = new StateCasting();
            var started = typeof(StateCasting).GetField("_started", BindingFlags.Instance | BindingFlags.NonPublic)!;
            GrayjaySettings.Instance.Casting.Enabled = false;
            state.Start();
            Assert.IsFalse((bool)started.GetValue(state)!);
            GrayjaySettings.Instance.Casting.Enabled = true;
            state.Start();
            state.Start();
            Assert.IsTrue((bool)started.GetValue(state)!);
            await Task.Delay(200);
            state.Dispose();
            Assert.ThrowsException<ObjectDisposedException>(() => state.Start());
            Assert.AreEqual(0, state.DiscoveredDevices.Count);
        }
        finally
        {
            GrayjaySettings.Instance.Casting.Enabled = wasEnabled;
            typeof(GrayjayServer).GetProperty(nameof(GrayjayServer.Instance))!.SetValue(null, previousServer);
        }
    }

    [TestMethod]
    public void DiscoveryUpdatesBeforeAvailabilityAndDuplicateNotificationsAreSafe()
    {
        var previousServer = GrayjayServer.Instance;
        _ = new GrayjayServer();
        try
        {
            using var state = new StateCasting();
            var update = typeof(StateCasting).GetMethod("UpdateDiscoveredDevice", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var original = new FCast.SenderSDK.DeviceInfo("receiver", FCast.SenderSDK.ProtocolType.FCast,
                new FCast.SenderSDK.IpAddr[] { new FCast.SenderSDK.IpAddr.V4(127, 0, 0, 1) }, 46899, new());
            update.Invoke(state, new object[] { original });
            var first = state.DiscoveredDevices.Single();
            update.Invoke(state, new object[] { original });
            Assert.AreSame(first, state.DiscoveredDevices.Single());
            var changed = original with { Port = 46900 };
            update.Invoke(state, new object[] { changed });
            Assert.AreEqual(46900, first.DeviceInfo.Port);
            state.Dispose();
            update.Invoke(state, new object[] { original });
            Assert.AreEqual(0, state.DiscoveredDevices.Count);
        }
        finally { typeof(GrayjayServer).GetProperty(nameof(GrayjayServer.Instance))!.SetValue(null, previousServer); }
    }
    [TestMethod]
    public async Task NativeSdkLoadPreservesVolumeAndIdentifiesDesktop()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var previousServer = GrayjayServer.Instance;
        _ = new GrayjayServer();
        try
        {
            using var state = new StateCasting();
            var device = state.CreateDevice(new CastingDeviceInfo { Id = "native-test", Name = "native-test", Type = CastProtocolType.FCast,
                Addresses = new() { "127.0.0.1" }, Port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port });
            Assert.ThrowsException<InvalidOperationException>(() => device.MediaLoadAsync("BUFFERED", "video/mp4", "http://test/video", TimeSpan.Zero, TimeSpan.Zero, "Test", ""));
            state.Connect(device);
            using var receiver = await listener.AcceptTcpClientAsync(timeout.Token);
            using var stream = receiver.GetStream();
            async Task Send(byte opcode, string json)
            {
                var body = System.Text.Encoding.UTF8.GetBytes(json);
                var packet = new byte[5 + body.Length];
                System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(packet, 1 + body.Length);
                packet[4] = opcode;
                body.CopyTo(packet, 5);
                await stream.WriteAsync(packet, timeout.Token);
            }
            async Task<JsonElement> Read(byte opcode)
            {
                while (true)
                {
                    var header = new byte[4];
                    await stream.ReadExactlyAsync(header, timeout.Token);
                    var length = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(header);
                    Assert.IsTrue(length > 0 && length < 1000000);
                    var packet = new byte[length];
                    await stream.ReadExactlyAsync(packet, timeout.Token);
                    if (packet[0] != opcode) continue;
                    return JsonDocument.Parse(packet.AsMemory(1)).RootElement.Clone();
                }
            }
            await Send(11, "{\"version\":3}");
            var identity = await Read(14);
            Assert.AreEqual("Grayjay Desktop", identity.GetProperty("appName").GetString());
            Assert.IsFalse(string.IsNullOrEmpty(identity.GetProperty("appVersion").GetString()));
            await Send(14, "{}");
            while (device.ConnectionState.State != CastConnectionState.Connected)
                await Task.Delay(10, timeout.Token);
            device.PlaybackState.SetVolume(0.35);
            await device.MediaLoadAsync("BUFFERED", "video/mp4", "http://test/video", TimeSpan.FromSeconds(12), TimeSpan.FromSeconds(60), "Test", "", 1.5);
            var play = await Read(1);
            Assert.AreEqual("http://test/video", play.GetProperty("url").GetString());
            Assert.AreEqual(12, play.GetProperty("time").GetDouble());
            Assert.AreEqual(1.5, play.GetProperty("speed").GetDouble());
            Assert.IsTrue(!play.TryGetProperty("volume", out var volume) || volume.ValueKind == JsonValueKind.Null);
            Assert.AreEqual(0.35, device.PlaybackState.Volume);
            device.Dispose();
            Assert.ThrowsException<ObjectDisposedException>(() => device.MediaPauseAsync());
        }
        finally { typeof(GrayjayServer).GetProperty(nameof(GrayjayServer.Instance))!.SetValue(null, previousServer); }
    }

    private sealed class Receiver : CastingDevice
    {
        public bool FailConnect;
        public int Stops;
        public override CastingDeviceInfo DeviceInfo { get; set; } = new() { Id = "fake", Name = "fake", Type = CastProtocolType.FCast, Addresses = new() { "127.0.0.1" }, Port = 0 };
        public override void Start()
        {
            if (FailConnect) throw new InvalidOperationException("Connection failed");
            ConnectionState.SetState(CastConnectionState.Connected);
        }
        public override void Stop() => Stops++;
    }

    [TestMethod]
    public void DisconnectAndFailedConnectClearActiveDeviceAndIgnoreOldCallbacks()
    {
        var previousServer = GrayjayServer.Instance;
        _ = new GrayjayServer();
        try
        {
            using var state = new StateCasting();
            var first = new Receiver();
            state.Connect(first);
            var oldHandler = (Action<CastConnectionState>)typeof(StateCasting).GetField("_activeStateHandler", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(state)!;
            var second = new Receiver();
            state.Connect(second);
            oldHandler(CastConnectionState.Disconnected);
            Assert.AreSame(second, state.ActiveDevice);
            second.ConnectionState.SetState(CastConnectionState.Disconnected);
            Assert.IsNull(state.ActiveDevice);
            Assert.AreEqual(1, second.Stops);
            state.Connect(second);
            Assert.ThrowsException<InvalidOperationException>(() => state.Connect(new Receiver { FailConnect = true }));
            Assert.IsNull(state.ActiveDevice);
        }
        finally { typeof(GrayjayServer).GetProperty(nameof(GrayjayServer.Instance))!.SetValue(null, previousServer); }
    }

}
