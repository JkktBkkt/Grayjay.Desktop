using System.Net;
using System.Xml.Linq;
using Grayjay.ClientServer;
using Grayjay.ClientServer.Casting;
using Grayjay.ClientServer.Controllers;
using Grayjay.ClientServer.States;
using Grayjay.ClientServer.Transcoding;
using Grayjay.Engine.Models.Detail;
using Grayjay.Engine.Models.Subtitles;
using Grayjay.Engine.Models.Video;
using Grayjay.Engine.Models.Video.Sources;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.WebUtilities;

namespace Grayjay.Desktop.Tests;

[TestClass]
[DoNotParallelize]
public class CastingSubtitleTests
{
    private sealed class Receiver(bool supports, bool accepts, CastProtocolType protocol = CastProtocolType.Chromecast) : CastingDevice
    {
        public readonly List<(string Type, string Url, double Position, double? Speed)> Loads = new();
        public readonly List<string> Subtitles = new();
        public int Disabled;
        public override Task<bool> DisableSubtitlesAsync() { Disabled++; return Task.FromResult(accepts); }
        public override CastingDeviceInfo DeviceInfo { get; set; } = new() { Id = "test", Name = "test", Addresses = new() { "127.0.0.1" }, Port = 0, Type = protocol };
        public override bool CanSetVolume => true;
        public override bool CanSetSpeed => true;
        public override IPEndPoint LocalEndPoint => new(IPAddress.Loopback, 0);
        public override bool SupportsExternalSubtitles => supports;
        public override Task<bool> AddSubtitleUrlAsync(string url, string? name) { Subtitles.Add(url); return Task.FromResult(accepts); }
        public override Task MediaLoadAsync(string streamType, string contentType, string contentId, TimeSpan position, TimeSpan duration, string? title, string thumbnailUrl, double? speed = null, CancellationToken cancellationToken = default)
        { Loads.Add((contentType, contentId, position.TotalSeconds, speed)); return Task.CompletedTask; }
        public override void Start() { }
        public override void Stop() { }
        public override Task MediaSeekAsync(TimeSpan time, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public override Task MediaStopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public override Task MediaPauseAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public override Task MediaResumeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public override Task ChangeVolumeAsync(double volume, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public override Task ChangeSpeedAsync(double speed, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    [TestMethod]
    public void RawDashCaptionsRetainLanguage()
    {
        var inject = typeof(DetailsController).GetMethod("InjectDashSubtitle", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        var manifest = (string)inject.Invoke(null, new object[] { "<MPD><Period></Period></MPD>", "http://test/sub", "nl", "Dutch" })!;
        var track = XDocument.Parse(manifest).Descendants("AdaptationSet").Single();
        Assert.AreEqual("nl", track.Attribute("lang")!.Value);
        Assert.AreEqual("Dutch", track.Element("Label")!.Value);
    }

    [DataTestMethod]
    [DataRow(null, "English", "en")]
    [DataRow("df", "English (auto-generated)", "en")]
    [DataRow("nl", "Captions", "nl")]
    [DataRow(null, "Nederlands", "nl")]
    [DataRow(null, "Captions", "und")]
    public void DownloadedCaptionLanguagesResolveWithoutGuessing(string? language, string name, string expected)
        => Assert.AreEqual(expected, SubtitleLanguage.Resolve(language, name));

    [TestMethod]
    public void DownloadsRetainSubtitleLanguage()
    {
        var original = new SubtitleSource { Name = "Captions", Language = "nl", Format = "text/vtt" };
        var local = LocalSubtitleSource.FromSource(original, "/test/captions.vtt");
        var restored = System.Text.Json.JsonSerializer.Deserialize<LocalSubtitleSource>(System.Text.Json.JsonSerializer.Serialize(local))!;
        Assert.AreEqual("nl", restored.Language);
    }

    [DataTestMethod]
    [DataRow("nl", "nl")]
    [DataRow("und", "und")]
    [DataRow("", "und")]
    public void UmpManifestCaptionsRetainLanguage(string language, string expected)
    {
        var inject = typeof(Grayjay.ClientServer.Sabr.Cast.UmpCasting).GetMethod("InjectSubtitleAdaptationSet",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        var manifest = (string)inject.Invoke(null, new object[] {
            "<MPD xmlns=\"urn:mpeg:dash:schema:mpd:2011\"><Period></Period></MPD>",
            "http://test/sub?x=1&y=2", "text/vtt", language, "Dutch & captions" })!;
        var track = XDocument.Parse(manifest).Descendants().Single(x => x.Name.LocalName == "AdaptationSet");
        Assert.AreEqual(expected, track.Attribute("lang")!.Value);
        Assert.AreEqual("Dutch & captions", track.Elements().Single(x => x.Name.LocalName == "Label").Value);
        Assert.IsTrue(track.Elements().Any(x => x.Name.LocalName == "Role" && x.Attribute("value")?.Value == "main"));
    }

    [DataTestMethod]
    [DataRow(false, true, true, false)]
    [DataRow(true, true, true, false)]
    [DataRow(false, false, false, false)]
    [DataRow(true, false, false, false)]
    [DataRow(false, true, false, false)]
    [DataRow(true, true, false, false)]
    [DataRow(false, false, false, true)]
    [DataRow(false, true, false, true)]
    [DataRow(false, true, true, true)]
    public async Task ProgressiveCaptionsUseSdkOrCachedFallback(bool remote, bool supports, bool accepts, bool fcast)
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(directory);
        var previousDirectory = Environment.CurrentDirectory;
        Environment.CurrentDirectory = directory;
        var previousServer = GrayjayServer.Instance;
        _ = new GrayjayServer();
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString("?windowId=" + Guid.NewGuid());
        using var state = context.GetState();
        WebApplication? origin = null;
        try
        {
            var path = Path.Combine(directory, "video.mp4");
            if (supports && accepts) File.WriteAllText(path, "direct media");
            else Assert.AreEqual(0, FFMPEG.ExecuteSafe(new[] { "-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", "color=size=160x90:rate=25", "-t", "1", "-c:v", "libx264", path }));
            var captions = Path.Combine(directory, "captions.vtt");
            File.WriteAllText(captions, "WEBVTT\n\n00:00:00.000 --> 00:00:01.000\nOriginal caption\n");
            var local = new VideoLocal(new PlatformVideoDetails { Url = "https://test/video" })
            {
                VideoSources = new() { new LocalVideoSource { FilePath = path, Container = "video/mp4" } },
                SubtitleSources = new() { new LocalSubtitleSource { FilePath = captions, Format = "text/vtt", Name = "English" } }
            };
            state.DetailsState.VideoLocal = local;
            state.DetailsState.VideoLoaded = local;
            var originRequests = 0;
            var failOriginOnce = 0;
            if (remote)
            {
                var builder = WebApplication.CreateBuilder();
                builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
                origin = builder.Build();
                origin.MapGet("/video.mp4", () => {
                    Interlocked.Increment(ref originRequests);
                    if (Interlocked.Exchange(ref failOriginOnce, 0) == 1) return (IResult)Results.StatusCode(500);
                    return Results.File(path, "video/mp4", enableRangeProcessing: true);
                });
                await origin.StartAsync();
                var url = origin.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First() + "/video.mp4";
                state.DetailsState.VideoLoaded = new PlatformVideoDetails
                {
                    Url = "https://test/video", Video = new VideoDescriptor { VideoSources = new IVideoSource[] { new VideoUrlSource { Url = url, Container = "video/mp4" } } }
                };
            }
            var castServer = GrayjayCastingServer.Instance;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (castServer.BaseUri == null) await Task.Delay(20, timeout.Token);
            var receiver = new Receiver(supports, accepts, fcast ? CastProtocolType.FCast : CastProtocolType.Chromecast);
            var selection = new CastingController.SourceSelection(0, -1, 0, !remote, false, true);
            if (remote && !supports)
            {
                failOriginOnce = 1;
                await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
                    CastingController.LoadSourceAsync(state, receiver, "BUFFERED", 0.25, 1, selection, "Video", "", 1.5));
            }
            await CastingController.LoadSourceAsync(state, receiver, "BUFFERED", 0.25, 1, selection, "Video", "", 1.5);
            Assert.AreEqual(supports ? 1 : 0, receiver.Subtitles.Count);
            Assert.AreEqual(supports && !accepts ? 2 : 1, receiver.Loads.Count);
            var loaded = receiver.Loads.Last();
            Assert.AreEqual(0.25, loaded.Position);
            Assert.AreEqual(1.5, loaded.Speed);
            using var client = new HttpClient(new HttpClientHandler { UseProxy = false });
            if (supports && accepts)
            {
                Assert.AreEqual("video/mp4", loaded.Type);
                StringAssert.Contains(await client.GetStringAsync(receiver.Subtitles.Single()), "Original caption");
                Assert.AreEqual(0, originRequests);
                return;
            }
            var manifest = await client.GetStringAsync(loaded.Url);
            Assert.AreEqual("application/dash+xml", loaded.Type);
            var xml = XDocument.Parse(manifest);
            var captionTrack = xml.Descendants().Single(x => x.Name.LocalName == "AdaptationSet" && x.Attribute("contentType")?.Value == "text");
            Assert.AreEqual("en", captionTrack.Attribute("lang")!.Value);
            Assert.IsTrue(captionTrack.Elements().Any(x => x.Name.LocalName == "Role" && x.Attribute("value")?.Value == "main"));
            var mediaUrl = xml.Descendants().First(x => x.Name.LocalName == "BaseURL").Value;
            var mediaId = QueryHelpers.ParseQuery(new Uri(mediaUrl).Query)["id"].ToString();
            using var media = state.LocalMedia.Open(mediaId).Stream;
            var cachedPath = media.Name;
            var modified = File.GetLastWriteTimeUtc(cachedPath);
            var requests = originRequests;
            await CastingController.LoadSourceAsync(state, receiver, "BUFFERED", 0.5, 1, selection, "Video", "");
            Assert.AreEqual(loaded.Url, receiver.Loads.Last().Url);
            Assert.AreEqual(manifest, await client.GetStringAsync(receiver.Loads.Last().Url));
            Assert.AreEqual(modified, File.GetLastWriteTimeUtc(cachedPath));
            Assert.AreEqual(requests, originRequests);
            selection = selection with { SubtitleIndex = -1 };
            await CastingController.LoadSourceAsync(state, receiver, "BUFFERED", 0.5, 1, selection, "Video", "");
            Assert.AreEqual("video/mp4", receiver.Loads.Last().Type);
        }
        finally
        {
            if (origin != null) await origin.DisposeAsync();
            await GrayjayCastingServer.StopAsync();
            typeof(GrayjayServer).GetProperty(nameof(GrayjayServer.Instance))!.SetValue(null, previousServer);
            Environment.CurrentDirectory = previousDirectory;
            Directory.Delete(directory, true);
        }
    }
    [DataTestMethod]
    [DataRow(true, true)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    public async Task CaptionChangesAndDisableDoNotReloadMedia(bool supports, bool accepts)
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(directory);
        var previousServer = GrayjayServer.Instance;
        _ = new GrayjayServer();
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString("?windowId=" + Guid.NewGuid());
        using var state = context.GetState();
        try
        {
            var local = new VideoLocal(new PlatformVideoDetails { Url = "https://test/video" });
            foreach (var name in new[] { "English", "Dutch" })
            {
                var path = Path.Combine(directory, name + ".vtt");
                File.WriteAllText(path, "WEBVTT\n\n00:00:00.000 --> 00:00:01.000\n" + name + "\n");
                local.SubtitleSources.Add(new LocalSubtitleSource { FilePath = path, Format = "text/vtt", Name = name });
            }
            state.DetailsState.VideoLoaded = local;
            state.DetailsState.VideoLocal = local;
            var server = GrayjayCastingServer.Instance;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (server.BaseUri == null) await Task.Delay(20, timeout.Token);
            var device = new Receiver(supports, accepts);
            Assert.AreEqual(supports && accepts, await CastingController.ChangeSubtitleAsync(state, device, 0, true));
            Assert.AreEqual(supports && accepts, await CastingController.ChangeSubtitleAsync(state, device, 1, true));
            Assert.AreEqual(supports && accepts, await CastingController.ChangeSubtitleAsync(state, device, -1, false));
            Assert.AreEqual(0, device.Loads.Count);
            Assert.AreEqual(supports ? 2 : 0, device.Subtitles.Count);
            Assert.AreEqual(supports ? 1 : 0, device.Disabled);
            if (supports)
            {
                using var client = new HttpClient(new HttpClientHandler { UseProxy = false });
                StringAssert.Contains(await client.GetStringAsync(device.Subtitles[0]), "English");
                StringAssert.Contains(await client.GetStringAsync(device.Subtitles[1]), "Dutch");
            }
        }
        finally
        {
            await GrayjayCastingServer.StopAsync();
            typeof(GrayjayServer).GetProperty(nameof(GrayjayServer.Instance))!.SetValue(null, previousServer);
            Directory.Delete(directory, true);
        }
    }

    [DataTestMethod]
    [DataRow(false, true, true, false)]
    [DataRow(true, true, true, false)]
    [DataRow(false, false, false, false)]
    [DataRow(true, false, false, false)]
    [DataRow(false, true, false, false)]
    [DataRow(true, true, false, false)]
    [DataRow(false, false, false, true)]
    [DataRow(true, false, false, true)]
    public async Task HlsCaptionsUseSdkOrPlayableVttRendition(bool variant, bool supports, bool accepts, bool selectedAudio)
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(directory);
        var previous = GrayjayServer.Instance;
        _ = new GrayjayServer();
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString("?windowId=" + Guid.NewGuid());
        using var state = context.GetState();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var origin = builder.Build();
        var requests = 0;
        origin.MapGet("/video.m3u8", () => {
            requests++;
            return variant ? "#EXTM3U\n#EXT-X-TARGETDURATION:10\n#EXTINF:10,\nsegment.ts\n#EXT-X-ENDLIST\n"
                : "#EXTM3U\n#EXT-X-MEDIA:TYPE=SUBTITLES,GROUP-ID=\"existing\",NAME=\"Original\",URI=\"original.m3u8\"\n#EXT-X-STREAM-INF:BANDWIDTH=1000000,SUBTITLES=\"existing\"\nmedia.m3u8\n#EXT-X-STREAM-INF:BANDWIDTH=2000000,SUBTITLES=\"other\"\nmedia-hd.m3u8\n";
        });
        await origin.StartAsync();
        try
        {
            var url = origin.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single() + "/video.m3u8";
            var captions = Path.Combine(directory, "captions.srt");
            File.WriteAllText(captions, "1\n00:00:00,000 --> 00:00:01,000\nCaption\n");
            var local = new VideoLocal(new PlatformVideoDetails { Url = "https://test/video" });
            local.SubtitleSources.Add(new LocalSubtitleSource { FilePath = captions, Format = "application/x-subrip", Name = "English" });
            state.DetailsState.VideoLocal = local;
            state.DetailsState.VideoLoaded = new PlatformVideoDetails { Url = "https://test/video", Duration = 10,
                Video = selectedAudio ? new UnMuxedVideoDescriptor {
                    VideoSources = new IVideoSource[] { new HLSManifestSource { Url = url } },
                    AudioSources = new IAudioSource[] { new HLSManifestAudioSource { Url = url.Replace("video.m3u8", "audio.m3u8") } }
                } : new VideoDescriptor { VideoSources = new IVideoSource[] { new HLSManifestSource { Url = url } } } };
            var server = GrayjayCastingServer.Instance;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (server.BaseUri == null) await Task.Delay(20, timeout.Token);
            var device = new Receiver(supports, accepts);
            await CastingController.LoadSourceAsync(state, device, "BUFFERED", 0, 10,
                new(0, selectedAudio ? 0 : -1, 0, false, false, true), "Video", "");
            Assert.AreEqual(supports && !accepts ? 2 : 1, device.Loads.Count);
            Assert.AreEqual("application/vnd.apple.mpegurl", device.Loads.Last().Type);
            Assert.AreEqual(0, requests);
            if (supports && accepts) return;
            var manifest = await DetailsController.GenerateSourceHLS(state, 0, selectedAudio ? 0 : -1, 0, true,
                new Grayjay.ClientServer.Proxy.ProxySettings(false, true, IPAddress.Loopback, true));
            var master = Grayjay.ClientServer.Parsers.HLS.ParseMasterPlaylist(manifest, server.BaseUri.ToString());
            Assert.AreEqual(variant ? 1 : 2, master.VariantPlaylistsRefs.Count);
            Assert.IsTrue(master.VariantPlaylistsRefs.All(v => v.StreamInfo.Bandwidth > 0));
            Assert.IsTrue(master.VariantPlaylistsRefs.All(v => v.StreamInfo.Codecs != "HLS"));
            var selected = master.MediaRenditions.Single(r => r.GroupId == "gj-subs");
            Assert.IsTrue(master.VariantPlaylistsRefs.All(v => v.StreamInfo.Subtitles == selected.GroupId));
            if (selectedAudio)
            {
                var audio = master.MediaRenditions.Single(r => r.GroupId == "gj-audio");
                Assert.IsTrue(master.VariantPlaylistsRefs.All(v => v.StreamInfo.Audio == audio.GroupId));
                StringAssert.Contains(QueryHelpers.ParseQuery(new Uri(audio.Uri).Query)["url"].ToString(), "audio.m3u8");
            }
            using var client = new HttpClient(new HttpClientHandler { UseProxy = false });
            var playlist = await client.GetStringAsync(selected.Uri);
            var captionUrl = playlist.Split('\n').Single(line => line.StartsWith("http"));
            var vtt = await client.GetStringAsync(captionUrl);
            StringAssert.StartsWith(vtt, "WEBVTT");
            StringAssert.Contains(vtt, "00:00:00.000 --> 00:00:01.000");
        }
        finally
        {
            await GrayjayCastingServer.StopAsync();
            typeof(GrayjayServer).GetProperty(nameof(GrayjayServer.Instance))!.SetValue(null, previous);
            Directory.Delete(directory, true);
        }
    }

    [TestMethod]
    public async Task DashSourceManifestCacheSeparatesLocalAndRemoteSubtitles()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(directory);
        var previous = GrayjayServer.Instance;
        _ = new GrayjayServer();
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString("?windowId=" + Guid.NewGuid());
        using var state = context.GetState();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var origin = builder.Build();
        origin.MapGet("/video.mpd", () => Results.Text("<MPD xmlns=\"urn:mpeg:dash:schema:mpd:2011\" type=\"static\"><Period><AdaptationSet contentType=\"video\"><Representation id=\"1\" bandwidth=\"1\"><BaseURL>video.mp4</BaseURL></Representation></AdaptationSet></Period></MPD>", "application/dash+xml"));
        await origin.StartAsync();
        try
        {
            var url = origin.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single() + "/video.mpd";
            var captions = Path.Combine(directory, "captions.vtt");
            File.WriteAllText(captions, "WEBVTT\n\n00:00:00.000 --> 00:00:01.000\nCaption\n");
            var local = new VideoLocal(new PlatformVideoDetails { Url = "https://test/video" });
            local.SubtitleSources.Add(new LocalSubtitleSource { FilePath = captions, Format = "text/vtt", Name = "English" });
            state.DetailsState.VideoLocal = local;
            state.DetailsState.VideoLoaded = new PlatformVideoDetails
            {
                Url = "https://test/video",
                Video = new VideoDescriptor { VideoSources = new IVideoSource[] { new DashManifestSource { Url = url } } },
                Subtitles = new[] { new SubtitleSource { Name = "Remote", Url = "https://test/remote.vtt", Format = "text/vtt" } }
            };
            var proxySettings = new Grayjay.ClientServer.Proxy.ProxySettings(true);
            var remoteManifest = await DetailsController.GetOrGenerateSourceDashUrl(state, 0, 0, false, proxySettings);
            var localManifest = await DetailsController.GetOrGenerateSourceDashUrl(state, 0, 0, true, proxySettings);
            StringAssert.Contains(remoteManifest, "subtitleIsLocal=False");
            StringAssert.Contains(localManifest, "subtitleIsLocal=True");
            Assert.AreEqual(remoteManifest, await DetailsController.GetOrGenerateSourceDashUrl(state, 0, 0, false, proxySettings));
        }
        finally
        {
            typeof(GrayjayServer).GetProperty(nameof(GrayjayServer.Instance))!.SetValue(null, previous);
            Directory.Delete(directory, true);
        }
    }

    [DataTestMethod]
    [DataRow(false, false, true)]
    [DataRow(false, false, false)]
    [DataRow(true, false, true)]
    [DataRow(false, true, false)]
    [DataRow(true, true, false)]
    [DataRow(true, true, true)]
    public async Task SeparateProgressiveAudioWithoutIndexesIsPreserved(bool videoLocal, bool audioLocal, bool nativeCaptions)
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(directory);
        var previous = GrayjayServer.Instance;
        _ = new GrayjayServer();
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString("?windowId=" + Guid.NewGuid());
        using var state = context.GetState();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var origin = builder.Build();
        try
        {
            var video = Path.Combine(directory, "video.mp4");
            var audio = Path.Combine(directory, "audio.m4a");
            var captions = Path.Combine(directory, "captions.vtt");
            Assert.AreEqual(0, FFMPEG.ExecuteSafe(new[] { "-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", "color=size=160x90:rate=25", "-t", "1", "-c:v", "libx264", video }));
            Assert.AreEqual(0, FFMPEG.ExecuteSafe(new[] { "-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", "sine=frequency=440", "-t", "1", "-c:a", "aac", audio }));
            File.WriteAllText(captions, "WEBVTT\n\n00:00:00.000 --> 00:00:01.000\nCaption\n");
            origin.MapGet("/video.mp4", () => Results.File(video, "video/mp4", enableRangeProcessing: true));
            origin.MapGet("/audio.m4a", () => Results.File(audio, "audio/mp4", enableRangeProcessing: true));
            await origin.StartAsync();
            var url = origin.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            var local = new VideoLocal(new PlatformVideoDetails { Url = "https://test/video" }) {
                VideoSources = new() { new LocalVideoSource { FilePath = video, Container = "video/mp4" } },
                AudioSources = new() { new LocalAudioSource { FilePath = audio, Container = "audio/mp4" } },
                SubtitleSources = new() { new LocalSubtitleSource { FilePath = captions, Format = "text/vtt", Name = "English" } }
            };
            state.DetailsState.VideoLocal = local;
            state.DetailsState.VideoLoaded = new PlatformVideoDetails { Url = "https://test/video", Video = new UnMuxedVideoDescriptor {
                VideoSources = new IVideoSource[] { new VideoUrlSource { Url = url + "/video.mp4", Container = "video/mp4" } },
                AudioSources = new IAudioSource[] { new AudioUrlSource { Url = url + "/audio.m4a", Container = "audio/mp4" } }
            } };
            var server = GrayjayCastingServer.Instance;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (server.BaseUri == null) await Task.Delay(20, timeout.Token);
            var device = new Receiver(nativeCaptions, true, videoLocal && audioLocal ? CastProtocolType.FCast : CastProtocolType.Chromecast);
            await CastingController.LoadSourceAsync(state, device, "BUFFERED", 0, 1,
                new(0, 0, 0, videoLocal, audioLocal, true), "Video", "");
            Assert.AreEqual(1, device.Loads.Count);
            using var client = new HttpClient(new HttpClientHandler { UseProxy = false });
            var body = await client.GetStringAsync(device.Loads.Single().Url);
            var manifest = XDocument.Parse(body);
            var tracks = manifest.Descendants().Where(e => e.Name.LocalName == "AdaptationSet").ToArray();
            Assert.IsTrue(tracks.Any(e => e.Attribute("contentType")?.Value == "video"));
            Assert.IsTrue(tracks.Any(e => e.Attribute("contentType")?.Value == "audio"));
            Assert.AreEqual(!nativeCaptions, tracks.Any(e => e.Attribute("contentType")?.Value == "text"));
            Assert.AreEqual(nativeCaptions ? 1 : 0, device.Subtitles.Count);
        }
        finally
        {
            await GrayjayCastingServer.StopAsync();
            typeof(GrayjayServer).GetProperty(nameof(GrayjayServer.Instance))!.SetValue(null, previous);
            Directory.Delete(directory, true);
        }
    }

}
