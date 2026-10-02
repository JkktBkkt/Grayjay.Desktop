using System.Net;
using System.Xml.Linq;
using Grayjay.ClientServer;
using Grayjay.ClientServer.Controllers;
using Grayjay.ClientServer.Proxy;
using Grayjay.ClientServer.States;
using Grayjay.Engine.Models.Detail;
using Grayjay.Engine.Models.Video.Sources;
using Microsoft.AspNetCore.Http;

namespace Grayjay.Desktop.Tests;

[TestClass]
[DoNotParallelize]
public class CastingNetworkTests
{
    [TestMethod]
    public void LinkLocalControlConnectionUsesTheRouteToReceiversIpv4ForMedia()
    {
        var linkLocal = IPAddress.Parse("fe80::1032:2b8a:3ecb:b515%2");
        var receiver = IPAddress.Parse("192.168.1.85");
        var local = IPAddress.Parse("192.168.1.138");
        var selected = Grayjay.ClientServer.Casting.CastingAddress.Select(linkLocal,
            new[] { IPAddress.Parse("fe80::85%2"), receiver }, address => {
                Assert.AreEqual(receiver, address);
                return local;
            });
        Assert.AreEqual(local, selected);
        Assert.IsFalse(selected.ToString().Contains('%'));
    }

    [TestMethod]
    public void LinkLocalMediaAddressUsesTheOperatingSystemsIpv4Route()
    {
        var selected = Grayjay.ClientServer.Casting.CastingAddress.Select(IPAddress.Parse("fe80::138%2"), new[] { IPAddress.Loopback });
        Assert.AreEqual(IPAddress.Loopback, selected);
    }

    [TestMethod]
    public void GlobalIpv6AndMappedIpv4RemainUsableWithoutARouteLookup()
    {
        foreach (var address in new[] { "fd00::138", "::ffff:192.168.1.138" })
        {
            var selected = Grayjay.ClientServer.Casting.CastingAddress.Select(IPAddress.Parse(address), Array.Empty<IPAddress>(),
                _ => throw new AssertFailedException("Unexpected route lookup"));
            Assert.AreEqual(address.StartsWith("::ffff:") ? "192.168.1.138" : address, selected.ToString());
        }
    }

    [TestMethod]
    public void LinkLocalOnlyReceiverDoesNotReceiveAnUnusableMediaUrl()
    {
        Assert.ThrowsException<InvalidOperationException>(() => Grayjay.ClientServer.Casting.CastingAddress.Select(
            IPAddress.Parse("fe80::138%2"), new[] { IPAddress.Parse("fe80::85%2") }));
    }

    [DataTestMethod]
    [DataRow(false, "127.0.0.1")]
    [DataRow(true, "127.0.0.1")]
    [DataRow(false, "::1")]
    [DataRow(true, "::1")]
    public async Task OfflineDashAndMediaAreReachableByReceiver(bool proxySources, string address)
    {
        if (address == "::1" && !System.Net.Sockets.Socket.OSSupportsIPv6)
            Assert.Inconclusive("IPv6 is not available on this host");
        var localAddress = IPAddress.Parse(address);
        string directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(directory);
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString("?windowId=" + Guid.NewGuid());
        using var state = context.GetState();
        var previous = GrayjayServer.Instance;
        _ = new GrayjayServer();
        try
        {
            string videoPath = Path.Combine(directory, "video");
            string audioPath = Path.Combine(directory, "audio");
            File.WriteAllText(videoPath, "video bytes");
            File.WriteAllText(audioPath, "audio bytes");
            var local = new VideoLocal(new PlatformVideoDetails { Url = "https://test/first" })
            {
                VideoSources = new() { new LocalVideoSource { FilePath = videoPath, Container = "video/mp4", Codec = "avc1", Duration = 60, MetaData = new() { FileInitStart = 0, FileInitEnd = 1, FileIndexStart = 2, FileIndexEnd = 3 } } },
                AudioSources = new() { new LocalAudioSource { FilePath = audioPath, Container = "audio/mp4", Codec = "mp4a", Duration = 60, MetaData = new() { FileInitStart = 0, FileInitEnd = 1, FileIndexStart = 2, FileIndexEnd = 3 } } }
            };
            state.DetailsState.VideoLoaded = local;
            state.DetailsState.VideoLocal = local;
            var castServer = GrayjayCastingServer.Instance;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (castServer.BaseUri == null) await Task.Delay(20, timeout.Token);
            var settings = new ProxySettings(false, proxySources, localAddress, true);
            var descriptor = await DetailsController.GenerateSourceProxy(state, 0, 0, -1, true, true, false, settings, forceReady: true);
            var host = address == "::1" ? "[::1]" : address;
            var url = $"http://{host}:{castServer.BaseUri.Port}" + descriptor.Url;
            using var client = new HttpClient(new HttpClientHandler { UseProxy = false });
            using var response = await client.GetAsync(url);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            var xml = XDocument.Parse(await response.Content.ReadAsStringAsync());
            using var head = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, url));
            Assert.AreEqual(response.Content.Headers.ContentLength, head.Content.Headers.ContentLength);
            var mediaUrls = xml.Descendants().Where(e => e.Name.LocalName == "BaseURL").Select(e => e.Value).ToArray();
            Assert.AreEqual(2, mediaUrls.Length);
            foreach (var mediaUrl in mediaUrls)
            {
                Assert.AreEqual(castServer.BaseUri.Port, new Uri(mediaUrl).Port);
                using var media = await client.GetAsync(mediaUrl);
                Assert.AreEqual(HttpStatusCode.OK, media.StatusCode);
                CollectionAssert.AreEqual(File.ReadAllBytes(mediaUrl.Contains("Audio") ? audioPath : videoPath), await media.Content.ReadAsByteArrayAsync());
                using var request = new HttpRequestMessage(HttpMethod.Get, mediaUrl);
                request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(1, 3);
                using var partial = await client.SendAsync(request);
                Assert.AreEqual(HttpStatusCode.PartialContent, partial.StatusCode);
                Assert.AreEqual(3L, partial.Content.Headers.ContentLength);
                using var mediaHead = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, mediaUrl));
                Assert.AreEqual(media.Content.Headers.ContentLength, mediaHead.Content.Headers.ContentLength);
            }
        }
        finally
        {
            await GrayjayCastingServer.StopAsync();
            typeof(GrayjayServer).GetProperty(nameof(GrayjayServer.Instance))!.SetValue(null, previous);
            Directory.Delete(directory, true);
        }
    }
    [TestMethod]
    public async Task NetworkProxyServesBothIPv4AndIPv6()
    {
        if (!System.Net.Sockets.Socket.OSSupportsIPv6)
            Assert.Inconclusive("IPv6 is not available on this host");
        using var proxy = new HttpProxy(new IPEndPoint(IPAddress.IPv6Any, 0));
        proxy.Start();
        Assert.ThrowsException<ArgumentException>(() => proxy.Add(new HttpProxyRegistryEntry()));
        using var client = new HttpClient(new HttpClientHandler { UseProxy = false });
        foreach (var address in new[] { IPAddress.Loopback, IPAddress.IPv6Loopback })
        {
            var url = proxy.Add(new HttpProxyRegistryEntry
            {
                Url = "http://test/media",
                RequestExecutor = _ => new HttpProxyResponse
                {
                    Version = "HTTP/1.1", StatusCode = 200,
                    Headers = new Grayjay.Engine.Models.HttpHeaders { { "Content-Length", "5" } },
                    Data = System.Text.Encoding.UTF8.GetBytes("media")
                }
            }, address);
            Assert.AreEqual("media", await client.GetStringAsync(url));
        }
    }

}
