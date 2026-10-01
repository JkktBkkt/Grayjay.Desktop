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
