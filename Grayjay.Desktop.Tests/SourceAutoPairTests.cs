using Grayjay.ClientServer.Controllers;
using Grayjay.Engine.Models.Video.Sources;

namespace Grayjay.Desktop.Tests;

[TestClass]
public class SourceAutoPairTests
{
    private const int FullHdPixelCount = 1920 * 1080;

    private static VideoUrlSource ClearVideo(int height) =>
        new VideoUrlSource() { Width = height * 16 / 9, Height = height, Container = "video/mp4", Url = "https://example.com/clear" + height };

    private static VideoUrlWidevineSource WidevineVideo(int height) =>
        new VideoUrlWidevineSource() { Width = height * 16 / 9, Height = height, Container = "video/mp4", Url = "https://example.com/widevine" + height };

    private static AudioUrlSource ClearAudio() =>
        new AudioUrlSource() { Container = "audio/mp4", Bitrate = 128000, Url = "https://example.com/clear-audio" };

    private static AudioUrlWidevineSource WidevineAudio() =>
        new AudioUrlWidevineSource() { Container = "audio/mp4", Bitrate = 128000, Url = "https://example.com/widevine-audio" };

    private static HLSManifestWidevineSource WidevineHlsVideo(string licenseUri = "https://license.example.com") =>
        new HLSManifestWidevineSource() { Url = "https://example.com/widevine.m3u8", LicenseUri = licenseUri };

    private static HLSManifestWidevineAudioSource WidevineHlsAudio(string licenseUri = "https://license.example.com", bool priority = false) =>
        new HLSManifestWidevineAudioSource() { Url = "https://example.com/widevine-audio.m3u8", LicenseUri = licenseUri, Priority = priority };

    private static (int VideoIndex, int AudioIndex) Select(List<IVideoSource> videoSources, List<IAudioSource>? audioSources, bool widevineAvailable = true) =>
        DetailsController.SelectAutoSourcePair(videoSources, audioSources, widevineAvailable, FullHdPixelCount, null);

    [TestMethod]
    public void ClearOnly_PicksBestVideoAndAudio()
    {
        var videoSources = new List<IVideoSource>() { ClearVideo(480), ClearVideo(1080) };
        var audioSources = new List<IAudioSource>() { ClearAudio() };

        Assert.AreEqual((1, 0), Select(videoSources, audioSources));
    }

    [TestMethod]
    public void NoAudioList_PicksVideoOnly()
    {
        var videoSources = new List<IVideoSource>() { ClearVideo(1080) };

        Assert.AreEqual((0, -1), Select(videoSources, null));
    }

    [TestMethod]
    public void WidevineVideo_PairsWithWidevineAudio()
    {
        var videoSources = new List<IVideoSource>() { WidevineVideo(1080), ClearVideo(480) };
        var audioSources = new List<IAudioSource>() { ClearAudio(), WidevineAudio() };

        Assert.AreEqual((0, 1), Select(videoSources, audioSources));
    }

    [TestMethod]
    public void WidevineVideoWithOnlyClearAudio_FallsBackToClearVideo()
    {
        var videoSources = new List<IVideoSource>() { WidevineVideo(1080), ClearVideo(480) };
        var audioSources = new List<IAudioSource>() { ClearAudio() };

        Assert.AreEqual((1, 0), Select(videoSources, audioSources));
    }

    [TestMethod]
    public void ClearVideoWithOnlyWidevineAudio_FallsBackToWidevineUrlVideo()
    {
        var videoSources = new List<IVideoSource>() { ClearVideo(1080), WidevineVideo(480) };
        var audioSources = new List<IAudioSource>() { WidevineAudio() };

        Assert.AreEqual((1, 0), Select(videoSources, audioSources));
    }

    [TestMethod]
    public void ClearVideoWithOnlyWidevineAudio_IgnoresWidevineManifestAsFallback()
    {
        var videoSources = new List<IVideoSource>() { ClearVideo(1080), new DashManifestWidevineSource() { Url = "https://example.com/manifest.mpd" } };
        var audioSources = new List<IAudioSource>() { WidevineAudio() };

        Assert.AreEqual((0, 0), Select(videoSources, audioSources));
    }

    [TestMethod]
    public void NoConsistentPair_KeepsBestVideoAndAudio()
    {
        var videoSources = new List<IVideoSource>() { WidevineVideo(1080) };
        var audioSources = new List<IAudioSource>() { ClearAudio() };

        Assert.AreEqual((0, 0), Select(videoSources, audioSources));
    }

    [TestMethod]
    public void WidevineManifest_GetsNoSeparateAudio()
    {
        var videoSources = new List<IVideoSource>() { new DashManifestWidevineSource() { Url = "https://example.com/manifest.mpd" } };
        var audioSources = new List<IAudioSource>() { WidevineAudio(), ClearAudio() };

        Assert.AreEqual((0, -1), Select(videoSources, audioSources));
    }

    [TestMethod]
    public void WidevineUnavailable_PrefersClearSources()
    {
        var videoSources = new List<IVideoSource>() { WidevineVideo(1080), ClearVideo(480) };
        var audioSources = new List<IAudioSource>() { WidevineAudio(), ClearAudio() };

        Assert.AreEqual((1, 1), Select(videoSources, audioSources, widevineAvailable: false));
    }

    [TestMethod]
    public void WidevineUrlVideo_SkipsPreferredWidevineHlsAudio()
    {
        var videoSources = new List<IVideoSource>() { WidevineVideo(1080) };
        var audioSources = new List<IAudioSource>() { WidevineHlsAudio(priority: true), WidevineAudio() };

        Assert.AreEqual((0, 1), Select(videoSources, audioSources));
    }

    [TestMethod]
    public void ClearVideoWithOnlyWidevineHlsAudio_DoesNotSwitchToWidevineUrlVideo()
    {
        var videoSources = new List<IVideoSource>() { ClearVideo(1080), WidevineVideo(480) };
        var audioSources = new List<IAudioSource>() { WidevineHlsAudio() };

        Assert.AreEqual((0, 0), Select(videoSources, audioSources));
    }

    [TestMethod]
    public void WidevineHlsVideo_GetsNoSeparateAudio()
    {
        var videoSources = new List<IVideoSource>() { WidevineHlsVideo() };
        var audioSources = new List<IAudioSource>() { WidevineHlsAudio(), WidevineHlsAudio("https://other-license.example.com") };

        Assert.AreEqual((0, -1), Select(videoSources, audioSources));
    }

    [TestMethod]
    public void SharesVideoLicenseConfig_SameConfig()
    {
        Assert.IsTrue(DetailsController.SharesVideoLicenseConfig(WidevineHlsVideo(), WidevineHlsAudio()));
    }

    [TestMethod]
    public void SharesVideoLicenseConfig_DifferentLicenseUri()
    {
        Assert.IsFalse(DetailsController.SharesVideoLicenseConfig(WidevineHlsVideo(), WidevineHlsAudio("https://other-license.example.com")));
    }

    [TestMethod]
    public void SharesVideoLicenseConfig_DifferentServiceCertificate()
    {
        var audioSource = WidevineHlsAudio();
        audioSource.ServiceCertificate = "Q0VSVA==";

        Assert.IsFalse(DetailsController.SharesVideoLicenseConfig(WidevineHlsVideo(), audioSource));
    }

    [TestMethod]
    public void SharesVideoLicenseConfig_AudioWithOwnLicenseExecutor()
    {
        var audioSource = WidevineHlsAudio();
        typeof(JSSource).GetProperty(nameof(JSSource.HasLicenseRequestExecutor))!.SetValue(audioSource, true);

        Assert.IsFalse(DetailsController.SharesVideoLicenseConfig(WidevineHlsVideo(), audioSource));
    }
}
