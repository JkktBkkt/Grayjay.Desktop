using Grayjay.ClientServer.Casting;
using Grayjay.ClientServer.Proxy;
using Grayjay.ClientServer.Sabr.Cast;
using Grayjay.Engine.Models.Video.Sources;
using Grayjay.ClientServer.States;
using Grayjay.Desktop.POC;
using Microsoft.AspNetCore.Mvc;

namespace Grayjay.ClientServer.Controllers
{
    [Route("[controller]/[action]")]
    public class CastingController : ControllerBase
    {
        [HttpGet]
        public List<CastingDeviceInfo> DiscoveredDevices()
        {
            return StateCasting.Instance.DiscoveredDevices.Select(v => v.DeviceInfo).ToList();
        }

        [HttpGet]
        public List<CastingDeviceInfo> PinnedDevices()
        {
            return StateCasting.Instance.PinnedDevices.ToList();
        }

        [HttpPost]
        public ActionResult<Guid> AddPinnedDevice([FromBody] CastingDeviceInfo castingDeviceInfo)
        {
            StateCasting.Instance.AddPinnedDevice(castingDeviceInfo);
            return Ok();
        }

        [HttpPost]
        public ActionResult RemovePinnedDevice([FromBody] CastingDeviceInfo castingDeviceInfo)
        {
            StateCasting.Instance.RemovePinnedDevice(castingDeviceInfo);
            return Ok();
        }

        [HttpGet]
        public async Task<IActionResult> Connect(string id)
        {
            var instance = GrayjayCastingServer.Instance; //TODO: Make a nicer way to ensure the instance gets created

            CastingDevice? castingDevice = StateCasting.Instance.DiscoveredDevices.FirstOrDefault(x => x.DeviceInfo.Id == id);
            if (castingDevice == null)
            {
                var pinnedDeviceInfo = StateCasting.Instance.PinnedDevices.FirstOrDefault(x => x.Id == id);
                if (pinnedDeviceInfo != null)
                    castingDevice = StateCasting.Instance.CreateDevice(pinnedDeviceInfo);
            }

            if (castingDevice != null)
                StateCasting.Instance.Connect(castingDevice);
            else
                StateCasting.Instance.Disconnect();

            return Ok();
        }

        [HttpGet]
        public IActionResult Disconnect()
        {
            UmpCasting.Stop();
            StateCasting.Instance.Disconnect();
            return Ok();
        }

        [HttpGet]
        public async Task<ActionResult> MediaSeek(double time, CancellationToken cancellationToken)
        {
            Task? task = StateCasting.Instance.ActiveDevice?.MediaSeekAsync(TimeSpan.FromSeconds(time), cancellationToken);
            if (task != null)
                await task;
            return Ok();
        }

        [HttpGet]
        public async Task<ActionResult> MediaStop(CancellationToken cancellationToken)
        {
            UmpCasting.Stop();
            Task? task = StateCasting.Instance.ActiveDevice?.MediaStopAsync(cancellationToken);
            if (task != null)
                await task;
            return Ok();
        }

        [HttpGet]
        public async Task<ActionResult> MediaPause(CancellationToken cancellationToken)
        {
            Task? task = StateCasting.Instance.ActiveDevice?.MediaPauseAsync(cancellationToken);
            if (task != null)
                await task;
            return Ok();
        }

        [HttpGet]
        public async Task<ActionResult> MediaResume(CancellationToken cancellationToken)
        {
            Task? task = StateCasting.Instance.ActiveDevice?.MediaResumeAsync(cancellationToken);
            if (task != null)
                await task;
            return Ok();
        }

        [HttpGet]
        public async Task<ActionResult> MediaLoad(string streamType, double resumePosition, double duration, int videoIndex, int audioIndex, int subtitleIndex, string? title, string thumbnailUrl, bool videoIsLocal = false, bool audioIsLocal = false, bool subtitleIsLocal = false, double? speed = null, CancellationToken cancellationToken = default, string? tag = null)
        {
            var activeDevice = StateCasting.Instance.ActiveDevice;
            if (activeDevice == null)
                return BadRequest("No active device.");

            (var castVideo, _, _) = DetailsController.GetSources(this.State(), videoIndex, audioIndex, subtitleIndex, videoIsLocal, audioIsLocal, subtitleIsLocal);
            if (castVideo is UMPSource umpSource)
            {
                var ump = await UmpCasting.PrepareAsync(this.State(), umpSource, activeDevice, resumePosition, subtitleIndex, subtitleIsLocal, this.State().DetailsState.UmpCastHeight, title, thumbnailUrl);
                Logger.i(nameof(CastingController), $"Started UMP casting '{ump.Url}'.");
                await UmpCasting.LoadAsync(activeDevice, ump, title, thumbnailUrl, speed, cancellationToken);
                return Ok();
            }

            UmpCasting.Stop();
            await LoadSourceAsync(this.State(), activeDevice, streamType, resumePosition, duration,
                new SourceSelection(videoIndex, audioIndex, subtitleIndex, videoIsLocal, audioIsLocal, subtitleIsLocal),
                title, thumbnailUrl, speed, cancellationToken, tag);
            return Ok();
        }

        [HttpGet]
        public async Task<ActionResult<bool>> ChangeSubtitle(int subtitleIndex, bool subtitleIsLocal = false)
        {
            var device = StateCasting.Instance.ActiveDevice;
            if (device == null || device.ConnectionState.State != CastConnectionState.Connected)
                return BadRequest("No connected device.");
            return await ChangeSubtitleAsync(this.State(), device, subtitleIndex, subtitleIsLocal);
        }

        public static async Task<bool> ChangeSubtitleAsync(WindowState state, CastingDevice device, int subtitleIndex, bool subtitleIsLocal)
        {
            if (!device.SupportsExternalSubtitles) return false;
            if (subtitleIndex < 0) return await device.DisableSubtitlesAsync();
            var (_, _, subtitle) = DetailsController.GetSources(state, -1, -1, subtitleIndex, false, false, subtitleIsLocal);
            if (subtitle == null) return false;
            var settings = new ProxySettings(false, device.DeviceInfo.Type != CastProtocolType.FCast,
                proxyAddress: device.MediaAddress, exposeLocalAsAny: true);
            var url = DetailsController.BuildSubtitleUrl(state, subtitleIndex, subtitleIsLocal, settings);
            return await device.AddSubtitleUrlAsync(url, subtitle.Name);
        }

        public readonly record struct SourceSelection(int VideoIndex, int AudioIndex, int SubtitleIndex, bool VideoIsLocal, bool AudioIsLocal, bool SubtitleIsLocal);

        public static async Task LoadSourceAsync(WindowState state, CastingDevice activeDevice, string streamType, double resumePosition, double duration,
            SourceSelection selection, string? title, string thumbnailUrl, double? speed = null, CancellationToken cancellationToken = default, string? tag = null)
        {
            var (videoIndex, audioIndex, subtitleIndex, videoIsLocal, audioIsLocal, subtitleIsLocal) = selection;
            var shouldProxy = activeDevice.DeviceInfo.Type != CastProtocolType.FCast;
            var mediaAddress = activeDevice.MediaAddress;
            var settings = new ProxySettings(false, shouldProxy, proxyAddress: mediaAddress, exposeLocalAsAny: true);
            var (video, audio, subtitle) = DetailsController.GetSources(state, videoIndex, audioIndex, subtitleIndex, videoIsLocal, audioIsLocal, subtitleIsLocal);
            // Checked here because the progressive path below remuxes sources without going through GenerateSourceProxy.
            if (DetailsController.AnyWidevine(video, audio))
                throw DetailsController.CreateCastDrmException();
            var progressive = (video is LocalVideoSource || video is VideoUrlSource)
                && (video.Container == "video/mp4" || video.Container == "video/webm");
            var castBase = $"http://{mediaAddress.ToUrlAddress()}:{GrayjayCastingServer.Instance.BaseUri!.Port}";

            async Task Load(DetailsController.SourceDescriptor descriptor)
            {
                var url = descriptor.Url.StartsWith("/") ? castBase + descriptor.Url : descriptor.Url;
                cancellationToken.ThrowIfCancellationRequested();
                Logger.i(nameof(CastingController), $"Started casting '{url}' with content type '{descriptor.Type}'.");
                await activeDevice.MediaLoadAsync(streamType, descriptor.Type, url, TimeSpan.FromSeconds(resumePosition), TimeSpan.FromSeconds(duration), title, thumbnailUrl, speed, cancellationToken);
            }

            var subtitleUrl = subtitle != null ? DetailsController.BuildSubtitleUrl(state, subtitleIndex, subtitleIsLocal, settings) : null;
            async Task<DetailsController.SourceDescriptor> Prepare(int captions)
            {
                var missingIndex = video is not IStreamMetaDataSource { MetaData: not null }
                    || (audio != null && audio is not IStreamMetaDataSource { MetaData: not null });
                var allLocal = video is LocalVideoSource && (audio == null || audio is LocalAudioSource);
                if (progressive && missingIndex && (audio != null || captions >= 0) && !allLocal)
                {
                    string VideoInput() => video is LocalVideoSource local ? local.FilePath
                        : DetailsController.DirectVideoUrlSource((VideoUrlSource)video!, videoIndex, false, new ProxySettings(true, true)).Url;
                    string? AudioInput() => audio switch {
                        LocalAudioSource local => local.FilePath,
                        AudioUrlSource remote => DetailsController.DirectAudioUrlSource(remote, audioIndex, false, new ProxySettings(true, true)).Url,
                        null => null,
                        _ => throw new NotSupportedException("Cannot combine progressive video with a non-progressive audio source.")
                    };
                    var key = video is LocalVideoSource lv ? state.LocalMedia.RegisterFile(lv.FilePath, lv.Container) : ((VideoUrlSource)video!).Url;
                    key += "\n" + (audio is LocalAudioSource la ? state.LocalMedia.RegisterFile(la.FilePath, la.Container) : (audio as AudioUrlSource)?.Url);
                    var manifest = await Transcoding.LocalDash.GenerateUnindexedAsync(state.LocalMedia, key, () => (VideoInput(), AudioInput()),
                        castBase, state.WindowID, captions >= 0 ? subtitleUrl : null, subtitle?.Format, subtitle?.Name, subtitle?.Language);
                    var id = state.LocalMedia.RegisterManifest(manifest);
                    return new DetailsController.SourceDescriptor($"/Details/LocalDash?id={id}&windowId={Uri.EscapeDataString(state.WindowID)}", "application/dash+xml");
                }
                return await DetailsController.GenerateSourceProxy(state, videoIndex, audioIndex, captions, videoIsLocal, audioIsLocal, captions >= 0 && subtitleIsLocal, settings, tag, forceReady: true);
            }
            if (subtitle != null && activeDevice.SupportsExternalSubtitles)
            {
                await Load(await Prepare(-1));
                if (await activeDevice.AddSubtitleUrlAsync(subtitleUrl!, subtitle.Name)) return;
                Logger.w(nameof(CastingController), "Receiver rejected external subtitles; falling back to manifest subtitles.");
            }
            await Load(await Prepare(subtitleIndex));
        }

        [HttpGet]
        public ActionResult<UmpCasting.QualityOptions?> UmpCastQualities()
        {
            return Ok(UmpCasting.GetQualityOptions());
        }

        [HttpGet]
        public async Task<ActionResult> SetUmpCastQuality(int height)
        {
            this.State().DetailsState.UmpCastHeight = height;
            await UmpCasting.ChangeQualityAsync(height);
            return Ok();
        }

        [HttpGet]
        public async Task<ActionResult> ChangeVolume(double volume, CancellationToken cancellationToken)
        {
            Task? task = StateCasting.Instance.ActiveDevice?.ChangeVolumeAsync(volume, cancellationToken);
            if (task != null)
                await task;
            return Ok();
        }

        [HttpGet]
        public async Task<ActionResult> ChangeSpeed(double speed, CancellationToken cancellationToken)
        {
            Task? task = StateCasting.Instance.ActiveDevice?.ChangeSpeedAsync(speed, cancellationToken);
            if (task != null)
                await task;
            return Ok();
        }
    }
}
