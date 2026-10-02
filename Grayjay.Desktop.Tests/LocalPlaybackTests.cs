using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging.Abstractions;
using Grayjay.Engine.Models.Subtitles;
using Grayjay.ClientServer;
using Grayjay.ClientServer.Controllers;
using Grayjay.ClientServer.Sabr;
using Grayjay.ClientServer.States;
using Grayjay.Engine.Models.Detail;
using Grayjay.Engine.Models.Video.Sources;

namespace Grayjay.Desktop.Tests;

[TestClass]
public class LocalPlaybackTests
{
    private string _directory = null!;
    [TestInitialize]
    public void Initialize() => Directory.CreateDirectory(_directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()));
    [TestCleanup]
    public void Cleanup() => Directory.Delete(_directory, true);

    private VideoLocal Download(string url)
    {
        string Write(string kind)
        {
            var path = Path.Combine(_directory, Guid.NewGuid() + kind);
            File.WriteAllText(path, kind + ":" + url);
            return path;
        }
        return new(new PlatformVideoDetails { Url = url })
        {
            VideoSources = new() { new LocalVideoSource { FilePath = Write("video"), Container = "video/mp4", Codec = "avc1", Duration = 60 } },
            AudioSources = new() { new LocalAudioSource { FilePath = Write("audio"), Container = "audio/mp4", Codec = "mp4a", Duration = 60 } },
            SubtitleSources = new() { new LocalSubtitleSource { FilePath = Write("subtitle"), Format = "text/vtt" } }
        };
    }

    private static string SourceUrl(WindowState state, bool audioOnly)
    {
        var method = typeof(DetailsController).GetMethod(audioOnly ? "LocalAudioSource" : "LocalVideoSource",
            BindingFlags.Static | BindingFlags.NonPublic)!;
        object source = audioOnly ? state.DetailsState.VideoLocal.AudioSources[0] : state.DetailsState.VideoLocal.VideoSources[0];
        return ((DetailsController.SourceDescriptor)method.Invoke(null, new[] { state, source })!).Url;
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ConsecutiveDownloadsAtTheSameIndexHaveDistinctPlaybackUrls(bool audioOnly)
    {
        using var state = new WindowState("test");
        state.DetailsState.VideoLocal = Download("https://platform.test/watch?id=1&name=first");
        var first = SourceUrl(state, audioOnly);
        Assert.AreEqual(first, SourceUrl(state, audioOnly));
        state.DetailsState.VideoLocal = Download("https://platform.test/watch?id=2&name=second");
        var second = SourceUrl(state, audioOnly);
        Assert.AreNotEqual(first, second);
        Assert.IsFalse(second.Contains("mediaUrl="));
    }

    [TestMethod]
    public void SwitchingThroughAudioOnlyIsNotNeededToReloadTheNextVideo()
    {
        using var state = new WindowState("test");
        state.DetailsState.VideoLocal = Download("https://bilibili.test/video/1");
        var first = SourceUrl(state, false);
        state.DetailsState.VideoLocal = Download("https://bitchute.test/video/2");
        var second = SourceUrl(state, false);
        state.DetailsState.VideoLocal = Download("https://podcasts.test/episode/3");
        var podcast = SourceUrl(state, true);
        Assert.AreNotEqual(first, second);
        Assert.AreNotEqual(second, podcast);
    }

    [TestMethod]
    [DoNotParallelize]
    public async Task DashMediaUrlsChangeWithTheDownloadedItem()
    {
        var previousServer = GrayjayServer.Instance;
        _ = new GrayjayServer();
        using var state = new WindowState("test");
        try
        {
            async Task<string> Manifest(string url)
            {
                state.DetailsState.ClearCachedDash();
                state.DetailsState.VideoLocal = Download(url);
                state.DetailsState.VideoLocal.VideoSources[0].MetaData = new() { FileInitStart = 0, FileInitEnd = 10, FileIndexStart = 11, FileIndexEnd = 20 };
                state.DetailsState.VideoLocal.AudioSources[0].MetaData = new() { FileInitStart = 0, FileInitEnd = 10, FileIndexStart = 11, FileIndexEnd = 20 };
                return await DetailsController.GenerateSourceDash(state, 0, 0, -1, true, true, true).Item1;
            }
            var first = await Manifest("https://platform.test/video/1");
            var second = await Manifest("https://platform.test/video/2");
            Assert.AreNotEqual(first, second);
            StringAssert.Contains(second, "StreamLocalVideoSource");
            StringAssert.Contains(second, "StreamLocalAudioSource");
            Assert.IsFalse(second.Contains("mediaUrl="));
            Assert.IsFalse(second.Contains("index=0"));
        }
        finally { typeof(GrayjayServer).GetProperty(nameof(GrayjayServer.Instance))!.SetValue(null, previousServer); }
    }

    private static DefaultHttpContext Context(string windowId)
    {
        var context = new DefaultHttpContext();
        context.Request.Headers["WindowID"] = windowId;
        context.Request.Method = "GET";
        context.Response.Body = new MemoryStream();
        return context;
    }

    [DataTestMethod]
    [DataRow("Video")]
    [DataRow("Audio")]
    [DataRow("Subtitle")]
    public async Task OldUrlServesOriginalBytesAndRangesAfterSwitch(string kind)
    {
        var context = Context(Guid.NewGuid().ToString());
        using var state = context.GetState();
        var first = Download("https://platform.test/first");
        state.DetailsState.VideoLocal = first;
        var source = kind switch
        {
            "Video" => first.VideoSources[0].FilePath,
            "Audio" => first.AudioSources[0].FilePath,
            _ => first.SubtitleSources[0].FilePath
        };
        var id = kind == "Subtitle" ? state.LocalMedia.RegisterFile(source, "text/vtt")
            : QueryHelpers.ParseQuery(new Uri("http://test" + SourceUrl(state, kind == "Audio")).Query)["id"].ToString();
        state.DetailsState.VideoLocal = Download("https://platform.test/second");
        var controller = new DetailsController { ControllerContext = new ControllerContext { HttpContext = context } };
        IActionResult Result() => kind switch
        {
            "Video" => controller.StreamLocalVideoSource(id),
            "Audio" => controller.StreamLocalAudioSource(id),
            _ => controller.StreamLocalSubtitleSource(id)
        };
        var executor = new FileStreamResultExecutor(NullLoggerFactory.Instance);
        var action = new ActionContext(context, new RouteData(), new ActionDescriptor());
        await executor.ExecuteAsync(action, (FileStreamResult)Result());
        CollectionAssert.AreEqual(File.ReadAllBytes(source), ((MemoryStream)context.Response.Body).ToArray());
        context.Response.Body = new MemoryStream();
        context.Request.Headers.Range = "bytes=1-4";
        await executor.ExecuteAsync(action, (FileStreamResult)Result());
        Assert.AreEqual(206, context.Response.StatusCode);
        CollectionAssert.AreEqual(File.ReadAllBytes(source)[1..5], ((MemoryStream)context.Response.Body).ToArray());
        File.Delete(source);
        var error = Assert.ThrowsException<BadHttpRequestException>(() => Result());
        Assert.AreEqual(404, error.StatusCode);
    }

    [TestMethod]
    public void ReplacedArtifactsAndDisposedBindingsFailInsteadOfServingNewBytes()
    {
        using var state = new WindowState("test");
        var first = Download("https://platform.test/first");
        var source = first.VideoSources[0];
        var id = state.LocalMedia.RegisterFile(source.FilePath, source.Container);
        File.WriteAllText(source.FilePath, "replacement");
        Assert.AreEqual(404, Assert.ThrowsException<BadHttpRequestException>(() => state.LocalMedia.Open(id)).StatusCode);
        var replacement = state.LocalMedia.RegisterFile(source.FilePath, source.Container);
        Assert.AreNotEqual(id, replacement);
        state.Dispose();
        Assert.AreEqual(404, Assert.ThrowsException<BadHttpRequestException>(() => state.LocalMedia.Open(replacement)).StatusCode);
        Assert.AreEqual(404, Assert.ThrowsException<BadHttpRequestException>(() => state.LocalMedia.RegisterFile(source.FilePath, source.Container)).StatusCode);
    }

    [TestMethod]
    public async Task ConvertedSubtitlesRetainTheirBindingAndRejectMissingIdentity()
    {
        using var state = new WindowState("test");
        var first = Download("https://platform.test/first");
        File.WriteAllText(first.SubtitleSources[0].FilePath, "WEBVTT\n\n00:00:00.000 --> 00:00:01.000\nfirst\n");
        state.DetailsState.VideoLocal = first;
        var id = DetailsController.RegisterLocalSubtitle(state, 0);
        state.DetailsState.VideoLocal = Download("https://platform.test/second");
        var (bytes, contentType) = await DetailsController.GetSubtitleBytesAsync(state, 0, true, localMediaId: id);
        Assert.AreEqual("text/vtt", contentType);
        StringAssert.Contains(System.Text.Encoding.UTF8.GetString(bytes), "first");
        var error = await Assert.ThrowsExceptionAsync<BadHttpRequestException>(() => DetailsController.GetSubtitleBytesAsync(state, 0, true));
        Assert.AreEqual(404, error.StatusCode);
    }

    [TestMethod]
    public void BindingsSurviveLaterSelectionsUntilWindowCloses()
    {
        using var state = new WindowState("test");
        var first = state.LocalMedia.RegisterManifest("first");
        for (var i = 0; i < 512; i++) state.LocalMedia.RegisterManifest(i.ToString());
        Assert.AreEqual("first", state.LocalMedia.GetManifest(first));
        Assert.AreEqual("511", state.LocalMedia.GetManifest(state.LocalMedia.RegisterManifest("511")));
    }

    [TestMethod]
    public async Task SourceSelectionForAnotherVideoIsRejected()
    {
        var context = Context(Guid.NewGuid().ToString());
        using var state = context.GetState();
        state.DetailsState.VideoLocal = Download("https://platform.test/second");
        state.DetailsState.VideoLoaded = state.DetailsState.VideoLocal;
        var controller = new DetailsController { ControllerContext = new ControllerContext { HttpContext = context } };
        var error = await Assert.ThrowsExceptionAsync<BadHttpRequestException>(() => controller.SourceProxy(0, -1, -1, true, true, true, url: "https://platform.test/first"));
        Assert.AreEqual(409, error.StatusCode);
        var autoError = await Assert.ThrowsExceptionAsync<BadHttpRequestException>(() => controller.SourceAuto("https://platform.test/first"));
        Assert.AreEqual(409, autoError.StatusCode);
    }

    [TestMethod]
    [DoNotParallelize]
    public async Task LocalDashUrlRetainsTheOriginalManifestAfterSwitch()
    {
        var previousServer = GrayjayServer.Instance;
        _ = new GrayjayServer();
        var context = Context(Guid.NewGuid().ToString());
        using var state = context.GetState();
        try
        {
            state.DetailsState.VideoLocal = Download("https://platform.test/first");
            state.DetailsState.VideoLoaded = state.DetailsState.VideoLocal;
            state.DetailsState.VideoLocal.VideoSources[0].MetaData = new() { FileInitStart = 0, FileInitEnd = 10, FileIndexStart = 11, FileIndexEnd = 20 };
            state.DetailsState.VideoLocal.AudioSources[0].MetaData = new() { FileInitStart = 0, FileInitEnd = 10, FileIndexStart = 11, FileIndexEnd = 20 };
            var descriptor = await DetailsController.GenerateSourceProxy(state, 0, 0, -1, true, true, true);
            StringAssert.StartsWith(descriptor.Url, "/Details/LocalDash?");
            var id = QueryHelpers.ParseQuery(new Uri("http://test" + descriptor.Url).Query)["id"].ToString();
            var expected = state.LocalMedia.GetManifest(id);
            state.DetailsState.VideoLocal = Download("https://platform.test/second");
            var controller = new DetailsController { ControllerContext = new ControllerContext { HttpContext = context } };
            Assert.AreEqual(expected, ((ContentResult)controller.LocalDash(id)).Content);
        }
        finally { typeof(GrayjayServer).GetProperty(nameof(GrayjayServer.Instance))!.SetValue(null, previousServer); }
    }

    [DataTestMethod]
    [DataRow("127.0.0.1")]
    [DataRow("::1")]
    [DoNotParallelize]
    public async Task ProgressiveDownloadProducesCastDashWithAudioAndSubtitles(string address)
    {
        var previousServer = GrayjayServer.Instance;
        var previousDirectory = Environment.CurrentDirectory;
        Environment.CurrentDirectory = _directory;
        _ = new GrayjayServer();
        var context = Context(Guid.NewGuid().ToString());
        using var state = context.GetState();
        try
        {
            var local = Download("https://platform.test/progressive");
            local.AudioSources.Clear();
            var video = local.VideoSources[0];
            Assert.AreEqual(0, Grayjay.ClientServer.Transcoding.FFMPEG.ExecuteSafe(new[] {
                "-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", "color=size=160x90:rate=25",
                "-f", "lavfi", "-i", "sine=frequency=440", "-t", "2", "-c:v", "libx264", "-c:a", "aac",
                "-f", "mp4", video.FilePath }));
            state.DetailsState.VideoLocal = local;
            state.DetailsState.VideoLoaded = local;
            var castServer = GrayjayCastingServer.Instance;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (castServer.BaseUri == null) await Task.Delay(20, timeout.Token);
            var settings = new Grayjay.ClientServer.Proxy.ProxySettings(false, true, System.Net.IPAddress.Parse(address), true);
            var descriptor = await DetailsController.GenerateSourceProxy(state, 0, -1, 0, true, true, true, settings, forceReady: true);
            Assert.AreEqual("application/dash+xml", descriptor.Type);
            var id = QueryHelpers.ParseQuery(new Uri("http://test" + descriptor.Url).Query)["id"].ToString();
            var manifest = state.LocalMedia.GetManifest(id);
            var document = System.Xml.Linq.XDocument.Parse(manifest);
            System.Xml.Linq.XNamespace ns = "urn:mpeg:dash:schema:mpd:2011";
            Assert.IsTrue(document.Descendants(ns + "AdaptationSet").Any(x => (string?)x.Attribute("contentType") == "audio"));
            Assert.AreEqual(2, document.Descendants(ns + "SegmentBase").Count());
            Assert.IsTrue(document.Descendants(ns + "SegmentBase").All(x => x.Attribute("indexRange") != null));
            var urls = document.Descendants(ns + "BaseURL").Select(x => x.Value).ToArray();
            Assert.AreEqual(3, urls.Length);
            using var client = new HttpClient(new HttpClientHandler { UseProxy = false });
            var host = address == "::1" ? "[::1]" : address;
            var castBase = $"http://{host}:{castServer.BaseUri.Port}";
            var reused = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
                Grayjay.ClientServer.Transcoding.LocalDash.GenerateAsync(state.LocalMedia, video, null, castBase, state.WindowID, urls.Last(), "text/vtt", local.SubtitleSources[0].Name)));
            foreach (var cached in reused) Assert.AreSame(manifest, cached);
            Assert.AreEqual(manifest, await client.GetStringAsync(castBase + descriptor.Url));
            foreach (var url in urls.Take(2))
            {
                StringAssert.StartsWith(url, castBase);
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 99);
                using var response = await client.SendAsync(request);
                Assert.AreEqual(System.Net.HttpStatusCode.PartialContent, response.StatusCode);
                Assert.AreEqual(100, (await response.Content.ReadAsByteArrayAsync()).Length);
            }
            var subtitleQuery = QueryHelpers.ParseQuery(new Uri(new Uri("http://test"), urls.Last()).Query);
            var subtitleId = subtitleQuery["localMediaId"].ToString();
            state.DetailsState.VideoLocal = Download("https://platform.test/next");
            StringAssert.Contains(await client.GetStringAsync(urls.Last()), "progressive");
            Assert.AreEqual(manifest, state.LocalMedia.GetManifest(id));
            using (var stream = state.LocalMedia.Open(subtitleId).Stream)
            using (var reader = new StreamReader(stream))
                StringAssert.Contains(await reader.ReadToEndAsync(), "progressive");
            foreach (var url in urls.Take(2))
            {
                var mediaId = QueryHelpers.ParseQuery(new Uri(new Uri("http://test"), url).Query)["id"].ToString();
                using var stream = state.LocalMedia.Open(mediaId).Stream;
                Assert.IsTrue(stream.Length > 100);
            }
            var again = await Grayjay.ClientServer.Transcoding.LocalDash.GenerateAsync(state.LocalMedia, video, null, "http://[::1]:1234", state.WindowID, null, null, null);
            StringAssert.Contains(again, "http://[::1]:1234/Details/StreamLocalVideoSource?");
            var cachedUrls = System.Xml.Linq.XDocument.Parse(again).Descendants(ns + "BaseURL").Select(x => x.Value).ToArray();
            Assert.AreEqual(2, cachedUrls.Length);
            for (var index = 0; index < 2; index++)
                Assert.AreEqual(QueryHelpers.ParseQuery(new Uri(new Uri("http://test"), urls[index]).Query)["id"].ToString(),
                    QueryHelpers.ParseQuery(new Uri(cachedUrls[index]).Query)["id"].ToString());
            File.SetLastWriteTimeUtc(video.FilePath, File.GetLastWriteTimeUtc(video.FilePath).AddMinutes(1));
            var replaced = await Grayjay.ClientServer.Transcoding.LocalDash.GenerateAsync(state.LocalMedia, video, null, "http://[::1]:1234", state.WindowID, null, null, null);
            var replacedUrl = System.Xml.Linq.XDocument.Parse(replaced).Descendants(ns + "BaseURL").First().Value;
            Assert.AreNotEqual(cachedUrls[0], replacedUrl);
        }
        finally
        {
            await GrayjayCastingServer.StopAsync();
            Environment.CurrentDirectory = previousDirectory;
            typeof(GrayjayServer).GetProperty(nameof(GrayjayServer.Instance))!.SetValue(null, previousServer);
        }
    }

    [TestMethod]
    [DoNotParallelize]
    public async Task PreparedDashSurvivesWindowCloseAndRepairsMissingArtifacts()
    {
        var previousDirectory = Environment.CurrentDirectory;
        Environment.CurrentDirectory = _directory;
        try
        {
            var video = new LocalVideoSource { FilePath = Path.Combine(_directory, "download.mp4"), Container = "video/mp4" };
            Assert.AreEqual(0, Grayjay.ClientServer.Transcoding.FFMPEG.ExecuteSafe(new[] {
                "-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", "color=size=160x90:rate=25",
                "-t", "1", "-c:v", "libx264", video.FilePath }));
            var prepared = Grayjay.ClientServer.Transcoding.LocalDash.PrepareDownload(video, null);
            var preparedTime = File.GetLastWriteTimeUtc(prepared);
            var baseXml = System.Xml.Linq.XDocument.Load(prepared);
            System.Xml.Linq.XNamespace ns = "urn:mpeg:dash:schema:mpd:2011";
            Assert.IsFalse(baseXml.Descendants(ns + "AdaptationSet").Any(x => (string?)x.Attribute("contentType") == "text"));
            var mediaName = baseXml.Descendants(ns + "BaseURL").First().Value;
            Assert.AreEqual(Path.GetFileName(mediaName), mediaName);
            var mediaPath = Path.Combine(Path.GetDirectoryName(prepared)!, mediaName);
            var mediaTime = File.GetLastWriteTimeUtc(mediaPath);
            using (var first = new LocalMediaRegistry())
                await Grayjay.ClientServer.Transcoding.LocalDash.GenerateAsync(first, video, null, "http://first", "first", "http://first/english.vtt", "text/vtt", "English");
            Assert.IsTrue(File.Exists(prepared));
            using (var second = new LocalMediaRegistry())
            {
                var manifest = await Grayjay.ClientServer.Transcoding.LocalDash.GenerateAsync(second, video, null, "http://second", "second", "http://second/french.vtt", "text/vtt", "French");
                StringAssert.Contains(manifest, "http://second/french.vtt");
                Assert.AreEqual(preparedTime, File.GetLastWriteTimeUtc(prepared));
                Assert.AreEqual(mediaTime, File.GetLastWriteTimeUtc(mediaPath));
            }
            File.Delete(mediaPath);
            using (var repaired = new LocalMediaRegistry())
                await Grayjay.ClientServer.Transcoding.LocalDash.GenerateAsync(repaired, video, null, "http://third", "third", null, null, null);
            Assert.IsTrue(File.Exists(mediaPath));
            Assert.AreEqual(prepared, Grayjay.ClientServer.Transcoding.LocalDash.PrepareDownload(video, null));
            File.SetLastWriteTimeUtc(video.FilePath, File.GetLastWriteTimeUtc(video.FilePath).AddMinutes(1));
            var changed = Grayjay.ClientServer.Transcoding.LocalDash.PrepareDownload(video, null);
            Assert.AreNotEqual(prepared, changed);
            Assert.IsTrue(File.Exists(prepared));
            Grayjay.ClientServer.Transcoding.LocalDash.DeleteDownloadCache(video.FilePath);
            Assert.IsFalse(Directory.Exists(video.FilePath + ".dash"));
            Assert.IsTrue(File.Exists(video.FilePath));
        }
        finally { Environment.CurrentDirectory = previousDirectory; }
    }

    [TestMethod]
    public void ReselectingAnOlderFileKeepsItsBindingAvailable()
    {
        using var state = new WindowState("test");
        var local = Download("https://platform.test/first");
        var source = local.VideoSources[0];
        var id = state.LocalMedia.RegisterFile(source.FilePath, source.Container);
        for (var i = 0; i < 511; i++) state.LocalMedia.RegisterManifest(i.ToString());
        Assert.AreEqual(id, state.LocalMedia.RegisterFile(source.FilePath, source.Container));
        state.LocalMedia.RegisterManifest("current playback manifest");
        var (stream, _) = state.LocalMedia.Open(id);
        using (stream)
        using (var reader = new StreamReader(stream))
            Assert.AreEqual(File.ReadAllText(source.FilePath), reader.ReadToEnd());
    }

    [TestMethod]
    public async Task CastingManifestSupportsGetAndHeadThroughProductionRoute()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        GrayjayCastingServer.MapLocalDashEndpoint(app);
        var context = Context(Guid.NewGuid().ToString());
        using var state = context.GetState();
        var text = "<MPD>é</MPD>";
        var id = state.LocalMedia.RegisterManifest(text);
        await app.StartAsync();
        try
        {
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            var url = address + "/details/LocalDash?id=" + id + "&windowId=" + state.WindowID;
            using var client = new HttpClient();
            using var get = await client.GetAsync(url);
            Assert.AreEqual(System.Net.HttpStatusCode.OK, get.StatusCode);
            Assert.AreEqual(text, await get.Content.ReadAsStringAsync());
            using var head = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, url));
            Assert.AreEqual(System.Net.HttpStatusCode.OK, head.StatusCode);
            Assert.AreEqual(get.Content.Headers.ContentLength, head.Content.Headers.ContentLength);
            Assert.AreEqual("application/dash+xml", head.Content.Headers.ContentType!.MediaType);
            Assert.AreEqual("", await head.Content.ReadAsStringAsync());
        }
        finally { await app.StopAsync(); }
    }

    [TestMethod]
    [DoNotParallelize]
    public async Task NativeUmpPlaybackBindsDownloadedSubtitlesBeforeServingThem()
    {
        var previousServer = GrayjayServer.Instance;
        _ = new GrayjayServer();
        var context = Context(Guid.NewGuid().ToString());
        using var state = context.GetState();
        try
        {
            var first = Download("https://platform.test/first");
            File.WriteAllText(first.SubtitleSources[0].FilePath, "WEBVTT\n\n00:00:00.000 --> 00:00:01.000\nfirst subtitle\n");
            state.DetailsState.VideoLocal = first;
            var source = new UMPSource { Url = "https://media.test/ump", UstreamerConfig = "", Duration = 60 };
            DetailsController.UmpSourceDescriptor(state, source, 0, 0, true, "test");
            var playback = UmpPlaybackRegistry.Get(state.DetailsState.UmpPlaybackId!)!;
            var query = QueryHelpers.ParseQuery(new Uri("http://test" + playback.SubtitleUrl).Query);
            Assert.IsFalse(string.IsNullOrEmpty(query["localMediaId"]));
            state.DetailsState.VideoLocal = Download("https://platform.test/second");
            var controller = new DetailsController { ControllerContext = new ControllerContext { HttpContext = context } };
            var result = (FileContentResult)await controller.Subtitle(0, true, localMediaId: query["localMediaId"]);
            Assert.AreEqual("text/vtt", result.ContentType);
            StringAssert.Contains(System.Text.Encoding.UTF8.GetString(result.FileContents), "first subtitle");
        }
        finally { typeof(GrayjayServer).GetProperty(nameof(GrayjayServer.Instance))!.SetValue(null, previousServer); }
    }
}
