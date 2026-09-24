using System.Security;
using System.Text.RegularExpressions;
using Grayjay.ClientServer.Casting;
using Grayjay.ClientServer.Controllers;
using Grayjay.ClientServer.States;
using Grayjay.Desktop.POC;
using Grayjay.Engine.Models.Video.Sources;

namespace Grayjay.ClientServer.Sabr.Cast
{
    public static class UmpCasting
    {
        private const string TAG = "UmpCasting";
        private const int CAST_AUTO_MAX_HEIGHT = 1080;

        public class ActiveCast
        {
            public required string Id { get; init; }
            public required SabrCastProxy Proxy { get; init; }
            public required CastingDevice Device { get; init; }
            public required UMPSource Source { get; init; }
            public required WindowState State { get; init; }
            public int PreferredHeight { get; init; } = -1;
            public int SubtitleIndex { get; init; } = -1;
            public bool SubtitleIsLocal { get; init; }
            public string? Title { get; set; }
            public string? ThumbnailUrl { get; set; }
            public string? SubtitleContentType { get; set; }
            public byte[]? SubtitleBytes { get; set; }
            public string? Manifest { get; set; }
            public string BaseUrl { get; set; } = "";
        }

        public class Result
        {
            public required string Url { get; init; }
            public required string ContentType { get; init; }
            public required string StreamType { get; init; }
            public double StartPosition { get; init; }
            public double Duration { get; init; }
        }

        private static readonly object _lock = new object();
        private static ActiveCast? _active;
        private static int _castId = 0;
        private static SabrSession.Transferable? _handBackState;
        private static string? _handBackVideoId;

        public static ActiveCast? Get(string id)
        {
            lock (_lock) return _active?.Id == id ? _active : null;
        }

        public static void Stop()
        {
            ActiveCast? active;
            lock (_lock)
            {
                active = _active;
                _active = null;
                _castId++;
            }
            if (active == null) return;
            var state = active.Proxy.ExportTransferable();
            lock (_lock)
            {
                _handBackState = state;
                _handBackVideoId = active.Proxy.VideoId;
            }
            active.Proxy.Release();
            Logger.i(TAG, $"Stopped UMP cast {active.Id}");
        }

        public static SabrSession.Transferable? TakeHandBackState(string videoId)
        {
            lock (_lock)
            {
                if (_handBackVideoId != videoId) return null;
                var state = _handBackState;
                _handBackState = null;
                _handBackVideoId = null;
                return state;
            }
        }

        private static int VideoCodecRank(string codecs)
        {
            var c = (codecs ?? "").ToLowerInvariant();
            if (c.StartsWith("avc1") || c.StartsWith("avc3")) return 0;
            if (c.StartsWith("vp9") || c.StartsWith("vp09")) return 1;
            if (c.StartsWith("av01")) return 2;
            if (c.StartsWith("vp8")) return 3;
            return 4;
        }

        private static int AudioCodecRank(string codecs)
        {
            var c = (codecs ?? "").ToLowerInvariant();
            if (c.StartsWith("mp4a")) return 0;
            if (c.StartsWith("opus")) return 1;
            if (c.StartsWith("vorbis")) return 2;
            return 3;
        }

        private static bool CastCanDecode(UMPFormat format) => !string.IsNullOrWhiteSpace(format.Codecs) && VideoCodecRank(format.Codecs) <= 1;

        public static UMPFormat? SelectCastVideoFormat(UMPSource source, int preferredHeight)
        {
            var decodable = source.VideoFormats.Where(CastCanDecode).ToList();
            if (decodable.Count == 0)
            {
                Logger.w(TAG, $"No cast-decodable video format (have: {string.Join(", ", source.VideoFormats.Select(x => x.Codecs))})");
                return null;
            }

            var pool = decodable.Where(x => x.Height > 0).ToList();
            if (pool.Count == 0) pool = decodable;

            UMPFormat? BestAt(int height) => pool.Where(x => x.Height == height)
                .OrderBy(x => VideoCodecRank(x.Codecs)).ThenByDescending(x => x.Bitrate).FirstOrDefault();

            UMPFormat? selected;
            if (preferredHeight > 0)
            {
                selected = BestAt(preferredHeight);
                if (selected == null)
                {
                    var lower = pool.Where(x => x.Height < preferredHeight).OrderByDescending(x => x.Height).FirstOrDefault();
                    if (lower != null) selected = BestAt(lower.Height);
                }
                if (selected == null)
                {
                    var higher = pool.Where(x => x.Height > preferredHeight).OrderBy(x => x.Height).FirstOrDefault();
                    if (higher != null) selected = BestAt(higher.Height);
                }
            }
            else
            {
                var capped = pool.Where(x => x.Height <= CAST_AUTO_MAX_HEIGHT).ToList();
                var cap = capped.Count > 0 ? capped.Max(x => x.Height) : pool.Min(x => x.Height);
                selected = BestAt(cap);
            }

            if (selected != null && VideoCodecRank(selected.Codecs) > 0)
                Logger.i(TAG, $"UMP cast using non-AVC video ({selected.Codecs} {selected.Height}p); older receivers may not decode this.");
            return selected;
        }

        public static UMPFormat? SelectCastAudioFormat(UMPSource source)
        {
            var decodable = source.AudioFormats.Where(x => !string.IsNullOrWhiteSpace(x.Codecs) && AudioCodecRank(x.Codecs) <= 2 && !x.IsDrc).ToList();
            if (decodable.Count == 0)
            {
                Logger.w(TAG, $"No cast-decodable audio format (have: {string.Join(", ", source.AudioFormats.Select(x => x.Codecs))})");
                return null;
            }
            var original = decodable.Where(x => x.IsOriginalAudio).ToList();
            var pool = original.Count > 0 ? original : decodable;
            return pool.OrderBy(x => AudioCodecRank(x.Codecs)).ThenByDescending(x => x.Bitrate).FirstOrDefault();
        }

        private static string EscapeXml(string value) => SecurityElement.Escape(value) ?? value;

        private static string InjectSubtitleAdaptationSet(string mpd, string subtitleUrl, string mimeType, string lang, string label)
        {
            var adaptation =
                $"<AdaptationSet contentType=\"text\" mimeType=\"{EscapeXml(mimeType)}\" lang=\"{EscapeXml(lang)}\" default=\"true\">\n" +
                "  <Role schemeIdUri=\"urn:mpeg:dash:role:2011\" value=\"subtitle\"/>\n" +
                $"  <Label>{EscapeXml(label)}</Label>\n" +
                $"  <Representation id=\"caption_0\" mimeType=\"{EscapeXml(mimeType)}\" lang=\"{EscapeXml(lang)}\" default=\"true\" bandwidth=\"1000\">\n" +
                $"    <BaseURL>{EscapeXml(subtitleUrl)}</BaseURL>\n" +
                "  </Representation>\n" +
                "</AdaptationSet>\n";
            var periodClose = new Regex("</Period\\s*>", RegexOptions.IgnoreCase);
            return periodClose.IsMatch(mpd) ? periodClose.Replace(mpd, adaptation + "</Period>", 1) : mpd;
        }

        public static string ManifestFor(ActiveCast cast)
        {
            if (!cast.Proxy.IsLive && cast.Manifest != null)
                return cast.Manifest;
            var b = cast.BaseUrl;
            var manifest = cast.Proxy.BuildManifest($"{b}/ump/cast/init?id={cast.Id}&amp;role=video", $"{b}/ump/cast/seg?id={cast.Id}&amp;role=video",
                $"{b}/ump/cast/init?id={cast.Id}&amp;role=audio", $"{b}/ump/cast/seg?id={cast.Id}&amp;role=audio", $"{b}/ump/cast/time?id={cast.Id}");
            if (manifest == null) return cast.Manifest ?? "";
            if (cast.SubtitleBytes != null && !cast.Proxy.IsLive)
                manifest = InjectSubtitleAdaptationSet(manifest, $"{b}/ump/cast/sub?id={cast.Id}", (cast.SubtitleContentType ?? "text/vtt").Split(';')[0].Trim(), "und", "Subtitles");
            cast.Manifest = manifest;
            return manifest;
        }

        public class QualityOption
        {
            public int Height { get; init; }
            public int Width { get; init; }
            public string Label { get; init; } = "";
            public string CodecName { get; init; } = "";
        }

        public class QualityOptions
        {
            public int SelectedHeight { get; init; } = -1;
            public int ActiveHeight { get; init; } = -1;
            public string? ActiveLabel { get; init; }
            public List<QualityOption> Options { get; init; } = new();
        }

        public static QualityOptions? GetQualityOptions()
        {
            ActiveCast? active;
            lock (_lock) active = _active;
            if (active == null) return null;
            var options = active.Source.VideoFormats.Where(x => x.Height > 0).Select(x => x.Height).Distinct().OrderByDescending(x => x)
                .Select(height => SelectCastVideoFormat(active.Source, height)).Where(x => x != null).Cast<UMPFormat>()
                .DistinctBy(x => x.Key)
                .Select(x => new QualityOption() { Height = x.Height, Width = x.Width, Label = x.QualityLabel, CodecName = x.CodecName })
                .ToList();
            var current = active.Proxy.VideoFormat;
            return new QualityOptions()
            {
                SelectedHeight = active.PreferredHeight,
                ActiveHeight = current?.Height ?? -1,
                ActiveLabel = current != null ? $"{current.QualityLabel} {current.CodecName}".Trim() : null,
                Options = options
            };
        }

        public static async Task<bool> ChangeQualityAsync(int preferredHeight)
        {
            ActiveCast? active;
            lock (_lock) active = _active;
            if (active == null) return false;
            if (active.PreferredHeight == preferredHeight) return true;

            var device = active.Device;
            var position = active.Proxy.IsLive ? 0 : Math.Max(device.PlaybackState.ExpectedCurrentTime.TotalSeconds, 0);
            var speed = device.PlaybackState.Speed > 0 ? device.PlaybackState.Speed : (double?)null;
            var result = await PrepareAsync(active.State, active.Source, device, position, active.SubtitleIndex, active.SubtitleIsLocal, preferredHeight, active.Title, active.ThumbnailUrl);
            await device.MediaLoadAsync(result.StreamType, result.ContentType, result.Url, TimeSpan.FromSeconds(result.StartPosition), TimeSpan.FromSeconds(result.Duration), active.Title, active.ThumbnailUrl ?? "", speed);
            return true;
        }

        public static async Task<Result> PrepareAsync(WindowState state, UMPSource source, CastingDevice device, double resumePosition, int subtitleIndex, bool subtitleIsLocal, int preferredHeight = -1, string? title = null, string? thumbnailUrl = null)
        {
            Stop();
            int castId;
            lock (_lock) castId = _castId;

            var video = SelectCastVideoFormat(source, preferredHeight);
            var audio = SelectCastAudioFormat(source);
            if (source.VideoFormats.Length > 0 && video == null)
                throw new InvalidOperationException("This video has no format the receiver can decode");
            if (source.AudioFormats.Length > 0 && audio == null)
                throw new InvalidOperationException("This video has no audio format the receiver can decode");

            var localId = state.DetailsState.UmpPlaybackId;
            var local = localId != null ? UmpPlaybackRegistry.Get(localId) : null;
            var restore = (local != null && local.Source.VideoId == source.VideoId && local.Session.FatalError == null) ? local.Session.ExportTransferable() : null;
            restore ??= TakeHandBackState(source.VideoId ?? "");

            var session = SabrStreamSpec.FromSource(source).CreateSession();
            if (restore != null) session.Restore(restore);
            var proxy = new SabrCastProxy(session, video, audio);

            proxy.PlayheadUs = () =>
            {
                lock (_lock) if (castId != _castId) return null;
                return (long)(Math.Max(device.PlaybackState.ExpectedCurrentTime.TotalSeconds, 0) * 1_000_000.0);
            };
            proxy.OnBackoff = delayMs =>
            {
                if (delayMs != null) Logger.i(TAG, $"UMP cast waiting {delayMs}ms on server backoff");
            };
            proxy.OnFatalError = error =>
            {
                lock (_lock) if (castId != _castId) return;
                var message = error switch
                {
                    SabrBlockedException => "Casting stopped: YouTube rejected the playback token. Reload the video and try again.",
                    SabrReloadRequiredException => "Casting stopped: the stream expired. Reload the video and try again.",
                    SabrFormatSubstitutedException => "Casting stopped: the stream format is out of sync. Update your plugins.",
                    _ => "Casting stopped: " + (error.Message ?? "stream failed")
                };
                StateUI.Toast(message);
                _ = device.MediaStopAsync();
                Stop();
            };
            proxy.OnReceiverLost = () =>
            {
                lock (_lock) if (castId != _castId) return;
                var target = proxy.ServableStartSeconds();
                if (target == null) return;
                Logger.i(TAG, $"Receiver drifted out of the servable window; seeking it to {target}s");
                _ = device.MediaSeekAsync(TimeSpan.FromSeconds(target.Value));
            };

            bool prepared;
            try
            {
                prepared = await proxy.PrepareAsync((long)(resumePosition * 1_000_000.0));
            }
            catch
            {
                proxy.Release();
                throw;
            }
            if (!prepared)
            {
                proxy.Release();
                throw new InvalidOperationException("Failed to prepare SABR cast stream");
            }

            var baseUrl = $"http://{device.LocalEndPoint?.Address.ToUrlAddress()}:{GrayjayCastingServer.Instance.BaseUri!.Port}";
            var cast = new ActiveCast()
            {
                Id = proxy.Id,
                Proxy = proxy,
                Device = device,
                BaseUrl = baseUrl,
                Source = source,
                State = state,
                PreferredHeight = preferredHeight,
                SubtitleIndex = subtitleIndex,
                SubtitleIsLocal = subtitleIsLocal,
                Title = title,
                ThumbnailUrl = thumbnailUrl
            };

            if (subtitleIndex >= 0 && !proxy.IsLive)
            {
                try
                {
                    var (bytes, contentType) = await DetailsController.GetSubtitleBytesAsync(state, subtitleIndex, subtitleIsLocal);
                    cast.SubtitleBytes = bytes;
                    cast.SubtitleContentType = contentType;
                }
                catch (Exception ex)
                {
                    Logger.w(TAG, "Failed to load subtitles for the UMP cast", ex);
                }
            }

            lock (_lock)
            {
                if (castId != _castId)
                {
                    proxy.Release();
                    throw new CastSupersededException("Superseded by a newer cast");
                }
                _active = cast;
            }

            var manifest = ManifestFor(cast);
            if (string.IsNullOrEmpty(manifest))
            {
                Stop();
                throw new InvalidOperationException("Failed to build SABR cast manifest");
            }

            var startPosition = proxy.IsLive ? (proxy.ServableStartSeconds() ?? 0.0)
                : (resumePosition == 0.0 && device is ChromecastCastingDevice ? 0.1 : resumePosition);
            var duration = !proxy.IsLive && proxy.DurationSeconds > 0 ? proxy.DurationSeconds : source.Duration;

            Logger.i(TAG, $"UMP cast ready id={cast.Id} live={proxy.IsLive} video={video?.Itag} audio={audio?.Itag} start={startPosition}");
            return new Result()
            {
                Url = $"{baseUrl}/ump/cast/manifest?id={cast.Id}",
                ContentType = "application/dash+xml",
                StreamType = proxy.IsLive ? "LIVE" : "BUFFERED",
                StartPosition = startPosition,
                Duration = duration
            };
        }
    }
}
