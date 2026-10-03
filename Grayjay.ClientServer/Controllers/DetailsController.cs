using GrayjayPlugin = Grayjay.Engine.GrayjayPlugin;
﻿using Grayjay.ClientServer.Browser;
using Grayjay.ClientServer.Database.Indexes;
using Grayjay.ClientServer.Exceptions;
using Grayjay.ClientServer.Helpers;
using Grayjay.ClientServer.LiveChat;
using Grayjay.ClientServer.Models;
using Grayjay.ClientServer.Models.Downloads;
using Grayjay.ClientServer.Pagers;
using Grayjay.ClientServer.Proxy;
using Grayjay.ClientServer.Sabr;
using Grayjay.ClientServer.Settings;
using Grayjay.ClientServer.States;
using Grayjay.ClientServer.Subscriptions;
using Grayjay.Desktop.POC;
using Grayjay.Desktop.POC.Port.States;
using Grayjay.Engine.Dash;
using Grayjay.Engine.Exceptions;
using Grayjay.Engine.Models.Comments;
using Grayjay.Engine.Models.Detail;
using Grayjay.Engine.Models.Feed;
using Grayjay.Engine.Models.Live;
using Grayjay.Engine.Models.Playback;
using Grayjay.Engine.Models.Subtitles;
using Grayjay.Engine.Models.Video;
using Grayjay.Engine.Models.Video.Additions;
using Grayjay.Engine.Models.Video.Sources;
using Grayjay.Engine.Pagers;
using Grayjay.Engine.V8;
using Grayjay.Engine.Web;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using System;
using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Web;
using System.Xml.Linq;

namespace Grayjay.ClientServer.Controllers
{
    [Route("[controller]/[action]")]
    public class DetailsController : ControllerBase
    {
        public class DetailsState : IDisposable
        {
            public PlatformPostDetails PostLoaded { get; set; }
            public PlatformVideoDetails VideoLoaded { get; set; }
            public VideoLocal VideoLocal { get; set; }
            public Subscription VideoSubscription { get; set; }
            public DBHistoryIndex VideoHistoryIndex { get; set; }
            public PlaybackTracker VideoPlaybackTracker { get; set; }

            public string? UmpPlaybackId { get; set; }
            public int UmpCastHeight { get; set; } = -1;

            public RequestExecutor _videoRequestExecutor = null;
            public RequestExecutor _audioRequestExecutor = null;

            private readonly Dictionary<DashManifestSource, string> _dashManifestLocations = new Dictionary<DashManifestSource, string>(ReferenceEqualityComparer.Instance);

            public string? GetDashManifestLocation(DashManifestSource source)
            {
                lock (_dashManifestLocations)
                    return _dashManifestLocations.TryGetValue(source, out var location) ? location : null;
            }

            public void SetDashManifestLocation(DashManifestSource source, string location)
            {
                lock (_dashManifestLocations)
                    _dashManifestLocations[source] = location;
            }

            public void ClearDashManifestLocations()
            {
                lock (_dashManifestLocations)
                    _dashManifestLocations.Clear();
            }

            private readonly Dictionary<DashManifestSource, DashSourceSession> _dashSourceSessions = new Dictionary<DashManifestSource, DashSourceSession>(ReferenceEqualityComparer.Instance);

            public DashSourceSession GetOrCreateDashSourceSession(DashManifestSource source)
            {
                lock (_dashSourceSessions)
                {
                    if (!_dashSourceSessions.TryGetValue(source, out var session))
                    {
                        session = new DashSourceSession();
                        _dashSourceSessions[source] = session;
                    }
                    return session;
                }
            }

            public void ClearDashSourceSessions()
            {
                lock (_dashSourceSessions)
                    _dashSourceSessions.Clear();
            }

            private readonly List<string> _dashRelativeProxyTokens = new List<string>();

            /// <summary>
            /// Creates and registers a DashRelative proxy only when generation is still the current cache generation, else returns null.
            /// A stale request creates nothing, as its proxy key can match a token the next video already uses.
            /// </summary>
            public string? TryCreateDashRelativeProxy(long generation, Func<string> createProxy)
            {
                lock (_dashRelativeProxyTokens)
                {
                    if (generation != CachedDashGeneration)
                    {
                        return null;
                    }
                    var token = createProxy();
                    if (!string.IsNullOrEmpty(token) && !_dashRelativeProxyTokens.Contains(token))
                        _dashRelativeProxyTokens.Add(token);
                    return token;
                }
            }

            public long _lastWatchPosition = 0;
            public DateTime _lastWatchPositionChange = DateTime.MinValue;
            public LiveChatManager? LiveChatManager { get; set; }
            private object _cachedDashLockObject = new object();
            private long _cachedDashGeneration = 0;
            public int CachedDashVideoIndex = -1;
            public int CachedDashAudioIndex = -1;
            public int CachedDashSubtitleIndex = -1;
            public bool CachedDashSubtitleIsLocal = false;
            public ProxySettings? CachedDashProxySettings = null;
            public Task<string>? CachedDashTask = null;

            //TODO: Either remove static or include window id somehow for cleanup.
            public static ConcurrentDictionary<string, IRequestModifier> Modifiers = new ConcurrentDictionary<string, IRequestModifier>();

            public RefPager<PlatformComment> CommentPager { get; set; }
            public ConcurrentDictionary<string, RefPager<PlatformComment>> RepliesPagers { get; set; } = new ConcurrentDictionary<string, RefPager<PlatformComment>>();


            public IPager<PlatformContent> RecommendationPager { get; set; }

            public string RegisterModifier(IRequestModifier modifier)
            {
                var id = Guid.NewGuid().ToString();
                Modifiers.AddOrUpdate(id, modifier, (a,b)=>modifier);
                return id;
            }

            /// <summary>
            /// Bumped by every ClearCachedDash, so a request that started before a video change cannot write into the next video's state.
            /// </summary>
            public long CachedDashGeneration
            {
                get
                {
                    lock (_cachedDashLockObject)
                    {
                        return _cachedDashGeneration;
                    }
                }
            }

            /// <summary>
            /// Bumps the generation and removes the DashRelative proxies in one critical section with TryCreateDashRelativeProxy,
            /// so a request on the new generation cannot register a proxy that this call then removes.
            /// </summary>
            public void ClearCachedDash()
            {
                lock (_dashRelativeProxyTokens)
                lock (_cachedDashLockObject)
                {
                    _cachedDashGeneration++;
                    foreach (var token in _dashRelativeProxyTokens)
                        ProxyController.RemoveDashRelativeProxy(token);
                    _dashRelativeProxyTokens.Clear();
                    CachedDashAudioIndex = -1;
                    CachedDashVideoIndex = -1;
                    CachedDashSubtitleIndex = -1;
                    CachedDashSubtitleIsLocal = false;
                    CachedDashTask = null;
                    CachedDashProxySettings = null;
                }
            }

            public Task<string>? GetCachedDashTask(int videoIndex, int audioIndex, int subtitleIndex, bool subtitleIsLocal, ProxySettings? proxySettings)
            {
                lock (_cachedDashLockObject)
                {
                    if (CachedDashVideoIndex == videoIndex && CachedDashAudioIndex == audioIndex && CachedDashSubtitleIndex == subtitleIndex && CachedDashSubtitleIsLocal == subtitleIsLocal && Equals(CachedDashProxySettings, proxySettings))
                        return CachedDashTask;
                    return null;
                }
            }

            public void SetCachedDash(int videoIndex, int audioIndex, int subtitleIndex, bool subtitleIsLocal, ProxySettings? proxySettings, Task<string> dash)
            {
                lock (_cachedDashLockObject)
                {
                    CachedDashVideoIndex = videoIndex;
                    CachedDashAudioIndex = audioIndex;
                    CachedDashSubtitleIndex = subtitleIndex;
                    CachedDashSubtitleIsLocal = subtitleIsLocal;
                    CachedDashTask = dash;
                    CachedDashProxySettings = proxySettings;
                }
            }

            /// <summary>
            /// Caches dash only when generation is still the current cache generation, and returns whether it did.
            /// </summary>
            public bool TrySetCachedDash(long generation, int videoIndex, int audioIndex, int subtitleIndex, bool subtitleIsLocal, ProxySettings? proxySettings, Task<string> dash)
            {
                lock (_cachedDashLockObject)
                {
                    if (generation != _cachedDashGeneration)
                    {
                        return false;
                    }
                    SetCachedDash(videoIndex, audioIndex, subtitleIndex, subtitleIsLocal, proxySettings, dash);
                    return true;
                }
            }

            public void ReleaseUmpPlayback()
            {
                var id = UmpPlaybackId;
                UmpPlaybackId = null;
                if (id != null)
                    UmpPlaybackRegistry.Release(id);
            }

            public void Dispose()
            {
                LiveChatManager?.Stop();
                LiveChatManager = null;
                ReleaseUmpPlayback();
                // Bumps the generation too, so a manifest request still in flight cannot register proxies after this.
                ClearCachedDash();
                ClearDashManifestLocations();
                ClearDashSourceSessions();
            }
        }

        /// <summary>
        /// HTTP session of one DASH manifest source, shared by its manifest, xlink and DashRelative proxy requests so cookies carry over.
        /// </summary>
        public sealed class DashSourceSession
        {
            public string Id { get; } = Guid.NewGuid().ToString("N");
            public ManagedHttpClient Client { get; } = new ManagedHttpClient();
        }

        static ManagedHttpClient _qualityClient = new ManagedHttpClient();

        private void ChangeVideo(PlatformVideoDetails video, VideoLocal videoLocal)
        {
            var state = this.State().DetailsState;
            video = video ?? videoLocal;
            // Assigned before the DASH generation bump, so a request that reads the new generation also reads the new video.
            state.VideoLoaded = video;
            state.VideoLocal = videoLocal;
            state.ClearCachedDash();
            state.ReleaseUmpPlayback();
            state.UmpCastHeight = -1;
            state.ClearDashManifestLocations();
            state.ClearDashSourceSessions();
            state.VideoSubscription = StateSubscriptions.GetSubscription(video?.Author?.Url ?? videoLocal?.Author?.Url);
            state.VideoHistoryIndex = video != null ? StateHistory.GetHistoryByVideo(video, true) : null;
            state.VideoPlaybackTracker?.onConcluded();
            try
            {
                state.VideoPlaybackTracker = video != null ? StatePlatform.GetPlaybackTracker(video.Url) : null;
            }
            catch (Exception ex)
            {
                state.VideoPlaybackTracker = null;
                Logger.e(nameof(DetailsController), "Failed to get Playback tracker", ex);
            }
            state._lastWatchPositionChange = DateTime.MinValue;
            state._lastWatchPosition = 0;

            state.LiveChatManager?.Stop();
            state.LiveChatManager = null;
            StateWebsocket.LiveEventsClear();
            if (video != null)
            {
                if (video.IsLive)
                {
                    try
                    {
                        var livePager = StatePlatform.GetLiveEvents(video.Url);
                        if (livePager != null)
                        {
                            state.LiveChatManager = new LiveChatManager(livePager);
                            state.LiveChatManager?.Follow(this, (liveEvents) =>
                            {
                                if (liveEvents.Count > 0)
                                    StateWebsocket.LiveEvents(liveEvents);
                            });
                            state.LiveChatManager?.Start();
                        }
                    }
                    catch (Exception e)
                    {
                        Logger.Error<DetailsController>("Failed to retrieve live chat events", e);
                        state.LiveChatManager?.Stop();
                        state.LiveChatManager = null;
                        StateWebsocket.LiveEvents(new List<PlatformLiveEvent>()
                    {
                        new LiveEventComment()
                        {
                            ColorName = "#FF0000",
                            Message = "Failed to load live stream because of an error: " + e.Message,
                            Name = "SYSTEM"
                        }
                    });
                    }
                }
                else if(video != null && video.HasVODEvents())
                {
                    try
                    {
                        var livePager = video.GetVODEvents();
                        if (livePager != null)
                        {
                            state.LiveChatManager = new LiveChatManager(livePager);
                            state.LiveChatManager?.Follow(this, (liveEvents) =>
                            {
                                if (liveEvents.Count > 0)
                                    StateWebsocket.LiveEvents(liveEvents);
                            });
                            state.LiveChatManager?.Start();
                        }
                    }
                    catch (Exception e)
                    {
                        Logger.Error<DetailsController>("Failed to retrieve vod chat events", e);
                        state.LiveChatManager?.Stop();
                        state.LiveChatManager = null;
                        StateWebsocket.LiveEvents(new List<PlatformLiveEvent>()
                    {
                        new LiveEventComment()
                        {
                            ColorName = "#FF0000",
                            Message = "Failed to load vod chat because of an error: " + e.Message,
                            Name = "SYSTEM"
                        }
                    });
                    }
                }
            }

        }

        private void ChangePost(PlatformPostDetails post)
        {
            var state = this.State().DetailsState;
            state.PostLoaded = post;
        }

        public static PlatformPostDetails EnsurePost(WindowState state)
            => state.DetailsState.PostLoaded ?? throw new BadHttpRequestException("No post loaded");
        public static PlatformVideoDetails EnsureVideo(WindowState state)
            => state.DetailsState.VideoLoaded ?? throw new BadHttpRequestException("No video loaded");
        public static PlatformVideoDetails EnsureVideoSelection(WindowState state, string? url)
        {
            var video = EnsureVideo(state);
            var local = state.DetailsState.VideoLocal;
            if ((url != null && !string.Equals(url, video.Url, StringComparison.Ordinal)) ||
                (local != null && !string.Equals(local.Url, video.Url, StringComparison.Ordinal)))
                throw new BadHttpRequestException("Source selection is obsolete", StatusCodes.Status409Conflict);
            return video;
        }
        public static VideoLocal EnsureLocal(WindowState state) => state.DetailsState.VideoLocal ?? throw new BadHttpRequestException("No offline video loaded");
        private RefPager<PlatformComment> EnsureComments()
            => this.State().DetailsState.CommentPager ?? throw new BadHttpRequestException("No comments loaded");
        private RefPager<PlatformComment> EnsureReplies(string reply)
            => (this.State().DetailsState.RepliesPagers.ContainsKey(reply) ? this.State().DetailsState.RepliesPagers[reply] : null) ?? throw new BadHttpRequestException("No replies loaded");
        private IPager<PlatformContent> EnsureRecommendations()
            => this.State().DetailsState.RecommendationPager ?? throw new BadHttpRequestException("No recommendations loaded");



        [HttpGet]
        public PostLoadResult PostLoad(string url)
        {
            Logger.i(nameof(DetailsController), "Loading: " + url);
            IPlatformContentDetails contentDetails = null;
            Exception contentDetailsException = null;
            try
            {
                contentDetails = StatePlatform.GetContentDetails(url);
            }
            catch(ScriptUnavailableException unex)
            {
                throw new DialogException(new ExceptionModel()
                {
                    Type = ExceptionModel.EXCEPTION_SCRIPT,
                    Title = "post was not available",
                    Message = unex?.Message ?? "Could not find the post, or it was otherwise not available",
                    CanRetry = true,
                    TypeName = nameof(ScriptUnavailableException)
                }, unex);
            }
            catch(ScriptCaptchaRequiredException captchaEx)
            {
                throw CreateCaptchaDialogException("post", captchaEx);
            }
            catch(Exception ex)
            {
                contentDetailsException = ex;
            }
            if(contentDetails is PlatformPostDetails post)
            {
                ChangePost(post);
            }
            else if(contentDetails == null)
            {
                ChangePost(null);
                if(contentDetailsException != null)
                {
                    throw new DialogException(ExceptionModel.FromException(contentDetailsException));
                }
            }
            else
            {
                ChangePost(null);
                throw new DialogException(new ExceptionModel()
                {
                    Type = ExceptionModel.EXCEPTION_GENERAL,
                    Title = "Unsupported Type",
                    Message = $"Unsupported content type [{contentDetails.GetType().Name}]",
                    CanRetry = false
                });
            }
            var state = this.State().DetailsState;
            return new PostLoadResult()
            {
                Post = state.PostLoaded
            };
        }
        [HttpGet]
        public PlatformPostDetails PostCurrent()
        {
            return this.State().DetailsState.PostLoaded;
        }

        [HttpGet]
        public VideoLoadResult VideoLoad(string url)
        {
            Logger.i(nameof(DetailsController), "Loading: " + url);
            VideoLocal local = StateDownloads.GetDownloadedVideo(url);
            IPlatformContentDetails contentDetails = null;
            Exception contentDetailsException = null;
            try
            {
                contentDetails = StatePlatform.GetContentDetails(url);
            }
            catch(ScriptUnavailableException unex)
            {
                throw new DialogException(new ExceptionModel()
                {
                    Type = ExceptionModel.EXCEPTION_SCRIPT,
                    Title = "Video was not available",
                    Message = unex?.Message ?? "Could not find the video, or it was otherwise not available",
                    CanRetry = true,
                    TypeName = nameof(ScriptUnavailableException)
                }, unex);
            }
            catch(ScriptCaptchaRequiredException captchaEx)
            {
                throw CreateCaptchaDialogException("video", captchaEx);
            }
            catch(Exception ex)
            {
                if (local != null)
                    ;// StateUI.Toast("Failed to get live video:\n" + ex.Message);
                contentDetailsException = ex;
            }
            if (local != null)
                StateUI.Toast("Offline video loaded");

            if (contentDetails is PlatformVideoDetails video)
            {
                ChangeVideo(video, local);
            }
            else if (local != null)
            {
                ChangeVideo(null, local);
            }
            else if (contentDetails == null)
            {
                ChangeVideo(null, null);
                Logger.e(nameof(DetailsController), "Failed to load video", contentDetailsException);
                if (contentDetailsException is TargetInvocationException targetInvocationException && targetInvocationException.InnerException != null)
                    contentDetailsException = targetInvocationException.InnerException;
                throw new DialogException(ExceptionModel.FromException("Video could not load", contentDetailsException));
            }
            else
            {
                ChangeVideo(null, null);
                throw new DialogException(new ExceptionModel()
                {
                    Type = ExceptionModel.EXCEPTION_GENERAL,
                    Title = "Unsupported Type",
                    Message = $"Unsupported content type [{contentDetails.GetType().Name}]",
                    CanRetry = false
                });
            }

            var state = this.State().DetailsState;
            return new VideoLoadResult()
            {
                Video = state.VideoLoaded,
                Local = state.VideoLocal
            };
        }

        [HttpGet]
        public PlatformVideoDetails VideoCurrent()
            => EnsureVideo(this.State());

        [HttpGet]
        public PagerResult<PlatformContent> RecommendationsLoad(string url)
        {
            var state = this.State().DetailsState;
            if(state.VideoLoaded != null)
            {
                state.RecommendationPager = state.VideoLoaded?.GetContentRecommendations();
                return state.RecommendationPager?.AsPagerResult();
            }
            return null;
        }
        [HttpGet]
        public PagerResult<PlatformContent> RecommendationsNextPage()
        {
            var recommend = EnsureRecommendations();
            recommend.NextPage();
            return recommend.AsPagerResult();
        }


        [HttpGet]
        public PagerResult<RefItem<PlatformComment>> CommentsLoad(string url)
        {
            try
            {
                var state = this.State().DetailsState;
                var pager = StatePlatform.GetComments(EnsureVideo(this.State()));
                state.CommentPager = new RefPager<PlatformComment>(pager);
                state.RepliesPagers = new ConcurrentDictionary<string, RefPager<PlatformComment>>();
                return state.CommentPager.AsPagerResult();
            }
            catch(Exception ex)
            {
                return new PagerResult<RefItem<PlatformComment>>()
                {
                    Exception = ex.Message
                };
            }
        }
        [HttpGet]
        public PagerResult<RefItem<PlatformComment>> CommentsNextPage()
        {
            var comments = EnsureComments();
            comments.NextPage();
            return comments.AsPagerResult();
        }
        [HttpGet]
        public PagerResult<RefItem<PlatformComment>> RepliesLoad(string commentId, string replyId = null)
        {
            var state = this.State().DetailsState;
            if (replyId == "undefined")
                replyId = null;
            var comments = (replyId == null) ? EnsureComments() : EnsureReplies(replyId);
            var comment = comments.FindRef(commentId, true);
            state.RepliesPagers[comment.RefID] = new RefPager<PlatformComment>(StatePlatform.GetSubComments(comment.Object));
            return state.RepliesPagers[comment.RefID].AsPagerResult();
        }
        [HttpGet]
        public PagerResult<RefItem<PlatformComment>> RepliesNextPage(string replyId)
        {
            var replies = EnsureReplies(replyId);
            replies.NextPage();
            return replies.AsPagerResult();
        }


        [HttpPost]
        public async Task ConfigureLiveChatView(int viewId, [FromBody] LiveChatWindowDescriptor descriptor)
        {
            if (!GrayjaySettings.Instance.Playback.UseLiveChatWindow || StateApp.MainWindow == null)
                throw new InvalidOperationException("Live chat webview is disabled or unavailable.");
            await StateApp.MainWindow.ConfigureLiveChatViewAsync(viewId, descriptor);
        }

        [HttpGet]
        public LiveChatWindowDescriptor? GetLiveChatWindow()
        {
            var video = EnsureVideo(this.State());
            if (!video.IsLive || !GrayjaySettings.Instance.Playback.UseLiveChatWindow)
                return null;

            return StatePlatform.GetLiveChatWindow(video.Url);
        }

        [HttpGet]
        public List<Chapter> GetVideoChapters(string url)
        {
            if (string.IsNullOrEmpty(url))
                return null;
            return StatePlatform.GetContentChapters(url);
        }


        [HttpGet]
        public IActionResult Download(string url, int videoIndex, int audioIndex)
        {
            var video = EnsureVideo(this.State());
            var sourceVideo = (videoIndex >= 0) ? video.Video.VideoSources[videoIndex] : null;
            var sourceAudio = (audioIndex >= 0 && video.Video is UnMuxedVideoDescriptor unmuxed) ? unmuxed.AudioSources[audioIndex] : null;

            VideoDownload existing = StateDownloads.GetDownloadingVideo(video.ID);
            
            //TODO: Edgecases
            if (existing != null)
                return BadRequest("Already downloaded");

            var download = StateDownloads.StartDownload(video, sourceVideo, sourceAudio);

            StateDownloads.StartDownloadCycle();

            return Ok(download);
        }

        [HttpGet]
        public List<VideoQuality> VideoQualities(int videoIndex)
        {
            var video = (videoIndex == -999) ? EnsureVideo(this.State()).Live :
                EnsureVideo(this.State()).Video.VideoSources[videoIndex];
            if (video is UMPSource)
                return new List<VideoQuality>();
            if(video is HLSManifestSource hlsVideo)
            {
                var hlsResponse = _qualityClient.GET(hlsVideo.Url, new Engine.Models.HttpHeaders());
                if (!hlsResponse.IsOk)
                    return new List<VideoQuality>();
                string hlsContent = hlsResponse.Body.AsString();
                if (Parsers.HLS.IsMediaPlaylist(hlsContent))
                    return new List<VideoQuality>();
                var hlsManifest = Parsers.HLS.ParseMasterPlaylist(hlsContent, hlsVideo.Url);
                return hlsManifest.GetVideoSources().Select(x => new VideoQuality()
                {
                    Name = $"({x.Width}x{x.Height}) " + x.Name,
                    Width = x.Width,
                    Height = x.Height
                }).ToList();

            }
            return new List<VideoQuality>();
        }
        public class VideoQuality
        {
            public string Name { get; set; }
            public int Width { get; set; }
            public int Height { get; set; }
        }

        public static (IVideoSource? Video, IAudioSource? Audio, ISubtitleSource? Subtitle) GetSources(WindowState state, int videoIndex, int audioIndex, int subtitleIndex, bool videoIsLocal, bool audioIsLocal, bool subtitleIsLocal)
            => (
                (!videoIsLocal) ?
                    ((videoIndex == -999) ? EnsureVideo(state).Live : 
                    ((videoIndex >= 0) ? EnsureVideo(state).Video.VideoSources[videoIndex] : null)) :
                    ((videoIndex >= 0) ? EnsureLocal(state).VideoSources[videoIndex] : null),
                (!audioIsLocal) ?
                    ((audioIndex >= 0 && EnsureVideo(state).Video is UnMuxedVideoDescriptor unmuxed) ? unmuxed.AudioSources[audioIndex] : null) :
                    ((audioIndex >= 0) ? EnsureLocal(state).AudioSources[audioIndex] : null),
                (!subtitleIsLocal) ?
                    ((subtitleIndex >= 0) ? (ISubtitleSource)EnsureVideo(state).Subtitles[subtitleIndex] : null) :
                    ((subtitleIndex >= 0) ? (ISubtitleSource)EnsureLocal(state).SubtitleSources[subtitleIndex] : null)
            );

        public (int VideoIndex, int AudioIndex) GetSourceIndexes(WindowState state, IVideoSource video, IAudioSource audio, bool videoIsLocal, bool audioIsLocal)
            => (
                (!videoIsLocal) ?
                    ((video != null) ? Array.IndexOf(EnsureVideo(state).Video.VideoSources, video) : -1) :
                    ((video != null) ? EnsureLocal(state).VideoSources.IndexOf((LocalVideoSource)video) : -1),
                (!audioIsLocal) ?
                    ((audio != null && EnsureVideo(state).Video is UnMuxedVideoDescriptor unmuxed) ? Array.IndexOf(unmuxed.AudioSources, audio) : -1) :
                    ((audio != null) ? EnsureLocal(state).AudioSources.IndexOf((LocalAudioSource)audio) : -1)
            );
        [HttpGet]
        public async Task<IActionResult> SourceDash(int videoIndex, int audioIndex, int subtitleIndex, bool videoIsLocal = false, bool audioIsLocal = false, bool subtitleIsLocal = false, bool isLoopback = true, string? tag = null)
        {
            var state = this.State();
            var underlying = state.DetailsState.VideoLoaded?.GetUnderlyingObject();
            if (!videoIsLocal && !audioIsLocal && underlying != null && GrayjayPlugin.GetEnginePlugin(underlying.Engine) == null)
                VideoLoad(state.DetailsState.VideoLoaded.Url);
            try
            {
                (var taskGenerateSourceDash, var promiseMetadata) = GenerateSourceDash(state, videoIndex, audioIndex, subtitleIndex, videoIsLocal, audioIsLocal, subtitleIsLocal, new ProxySettings(isLoopback));
                if(!taskGenerateSourceDash.IsCompleted && promiseMetadata != null)
                    StateWebsocket.VideoLoader("", promiseMetadata.EstimateDuration, state.WindowID, tag);
                var dash = await taskGenerateSourceDash;
                StateWebsocket.VideoLoaderFinish(state.WindowID, tag);
                return Content(dash, "application/dash+xml");
            }
            catch (ScriptReloadRequiredException reloadEx)
            {
                await StatePlatform.HandleReloadRequired(reloadEx);
                this.VideoLoad(state.DetailsState.VideoLoaded.Url);
                return await SourceDash(videoIndex, audioIndex, subtitleIndex, videoIsLocal, audioIsLocal, subtitleIsLocal, isLoopback, tag);
            }
            catch (Exception ex)
            {
                throw;
            }
        }
        public static (Task<string>, V8PromiseMetadata?) GenerateSourceDash(WindowState state, int videoIndex, int audioIndex, int subtitleIndex, bool videoIsLocal = false, bool audioIsLocal = false, bool subtitleIsLocal = false, ProxySettings? proxySettings = null)
        {
            var cachedDashTask = videoIsLocal || audioIsLocal || subtitleIsLocal ? null : state.DetailsState.GetCachedDashTask(videoIndex, audioIndex, subtitleIndex, subtitleIsLocal, proxySettings);
            if (cachedDashTask != null)
            {
                Logger.w<DetailsController>("Using cached DASH.");
                return (cachedDashTask, null);
            }

            (var sourceVideo, var sourceAudio, var sourceSubtitle) = GetSources(state, videoIndex, audioIndex, subtitleIndex, videoIsLocal, audioIsLocal, subtitleIsLocal);
            if (sourceVideo is DashManifestRawSource videoRawSource && sourceAudio is DashManifestRawAudioSource audioRawSource)
            {
                if(sourceSubtitle != null)
                    sourceSubtitle = SubtitleToProxied(state, sourceSubtitle, subtitleIsLocal, subtitleIndex, proxySettings);

                V8PromiseMetadata? metadata = null;
                var task = GenerateSourceDashRaw(state, videoRawSource, audioRawSource, sourceSubtitle, proxySettings, out metadata);
                state.DetailsState.SetCachedDash(videoIndex, audioIndex, subtitleIndex, subtitleIsLocal, proxySettings, task);
                return (task, metadata);
            }
            else if(sourceVideo is DashManifestRawSource videoRawSource2)
            {
                if (sourceSubtitle != null)
                    sourceSubtitle = SubtitleToProxied(state, sourceSubtitle, subtitleIsLocal, subtitleIndex, proxySettings);

                V8PromiseMetadata? metadata = null;
                var task = GenerateSourceDashRaw(state, videoRawSource2, null, sourceSubtitle, proxySettings, out metadata);
                state.DetailsState.SetCachedDash(videoIndex, audioIndex, subtitleIndex, subtitleIsLocal, proxySettings, task);
                return (task, metadata);
            }


            if (sourceVideo != null && !(sourceVideo is VideoUrlSource || sourceVideo is LocalVideoSource))
                throw new NotImplementedException();

            if (sourceAudio != null && !(sourceAudio is AudioUrlSource || sourceAudio is LocalAudioSource))
                throw new NotImplementedException();

            string? videoUrl;
            if (videoIsLocal)
            {
                if (proxySettings != null && proxySettings.Value.ExposeLocalAsAny && proxySettings.Value.ProxyAddress != null && sourceVideo != null)
                    videoUrl = $"http://{proxySettings.Value.ProxyAddress.ToUrlAddress()}:{GrayjayCastingServer.Instance.BaseUri!.Port}{LocalSourceUrl(state, "Video", ((LocalVideoSource)sourceVideo).FilePath, sourceVideo.Container)}";
                else if (sourceVideo != null)
                    videoUrl = $"{GrayjayServer.Instance.BaseUrl}{LocalSourceUrl(state, "Video", ((LocalVideoSource)sourceVideo).FilePath, sourceVideo.Container)}";
                else
                    videoUrl = null;
            }
            else if (proxySettings != null && proxySettings.Value.ShouldProxySources(sourceVideo, sourceAudio))
            {
                var modifier = (sourceVideo is JSSource jsS) ? jsS.GetRequestModifier() : null;
                var executor = (sourceVideo is JSSource jsS2) ? jsS2.GetRequestExecutor() : null;

                videoUrl = sourceVideo != null
                    ? WebUtility.HtmlEncode(HttpProxy.Get(proxySettings.Value.IsLoopback).Add(new HttpProxyRegistryEntry() {
                        RequestModifier = modifier?.ToProxyFunc(),
                        Url = (sourceVideo as VideoUrlSource)!.Url 
                    }, proxySettings?.ProxyAddress))
                    : null;
            }
            else
            {
                videoUrl = sourceVideo != null
                    ? (sourceVideo as VideoUrlSource)!.Url
                    : null;
            }

            string? audioUrl;
            if (audioIsLocal)
            {
                if (proxySettings != null && proxySettings.Value.ExposeLocalAsAny && proxySettings.Value.ProxyAddress != null && sourceAudio != null)
                    audioUrl = $"http://{proxySettings.Value.ProxyAddress.ToUrlAddress()}:{GrayjayCastingServer.Instance.BaseUri!.Port}{LocalSourceUrl(state, "Audio", ((LocalAudioSource)sourceAudio).FilePath, sourceAudio.Container)}";
                else if (sourceAudio != null)
                    audioUrl = $"{GrayjayServer.Instance.BaseUrl}{LocalSourceUrl(state, "Audio", ((LocalAudioSource)sourceAudio).FilePath, sourceAudio.Container)}";
                else
                    audioUrl = null;
            }
            else if (proxySettings != null && proxySettings.Value.ShouldProxySources(sourceVideo, sourceAudio))
            {
                var modifier = (sourceAudio is JSSource jsS) ? jsS.GetRequestModifier() : null;
                var executor = (sourceAudio is JSSource jsS2) ? jsS2.GetRequestExecutor() : null;
                audioUrl = sourceAudio != null ? WebUtility.HtmlEncode(HttpProxy.Get(proxySettings.Value.IsLoopback).Add(new HttpProxyRegistryEntry()
                {
                    RequestModifier = modifier?.ToProxyFunc(),
                    Url = (sourceAudio as AudioUrlSource)!.Url
                }, proxySettings?.ProxyAddress)) : null;
            }
            else
            {
                audioUrl = sourceAudio != null
                    ? (sourceAudio as AudioUrlSource)!.Url
                    : null;
            }

            string? subtitleUrl;
            if (sourceSubtitle != null)
            {
                if (subtitleIsLocal)
                {
                    if (proxySettings != null && proxySettings.Value.ExposeLocalAsAny && proxySettings.Value.ProxyAddress != null && sourceSubtitle != null)
                        subtitleUrl = $"http://{proxySettings.Value.ProxyAddress.ToUrlAddress()}:{GrayjayCastingServer.Instance.BaseUri!.Port}{LocalSourceUrl(state, "Subtitle", ((LocalSubtitleSource)sourceSubtitle).FilePath, sourceSubtitle.Format ?? "text/vtt")}";
                    else
                        subtitleUrl = $"{GrayjayServer.Instance.BaseUrl}{LocalSourceUrl(state, "Subtitle", ((LocalSubtitleSource)sourceSubtitle).FilePath, sourceSubtitle.Format ?? "text/vtt")}";
                }
                else
                {
                    var uri = sourceSubtitle.GetSubtitlesUri()!;
                    if (uri.Scheme == "file")
                    {
                        if (proxySettings != null && proxySettings.Value.ExposeLocalAsAny && proxySettings.Value.ProxyAddress != null)
                        {
                            subtitleUrl = sourceSubtitle != null
                                ? WebUtility.HtmlEncode(HttpProxy.Get(proxySettings.Value.IsLoopback).Add(new HttpProxyRegistryEntry() { Url = $"http://{proxySettings.Value.ProxyAddress.ToUrlAddress()}:{GrayjayCastingServer.Instance.BaseUri!.Port}/Details/StreamSubtitleFile?index={subtitleIndex}&windowId={state.WindowID}" }, proxySettings?.ProxyAddress))
                                : null;
                        }
                        else
                            subtitleUrl = $"{GrayjayServer.Instance.BaseUrl}/Details/StreamSubtitleFile?index={subtitleIndex}&windowId={state.WindowID}";
                    }
                    else
                    {
                        if (proxySettings != null && proxySettings.Value.ShouldProxySources(sourceVideo, sourceAudio))
                        {
                            subtitleUrl = sourceSubtitle != null
                                ? WebUtility.HtmlEncode(HttpProxy.Get(proxySettings.Value.IsLoopback).Add(new HttpProxyRegistryEntry() { Url = uri.ToString() }, proxySettings?.ProxyAddress))
                                : null;
                        }
                        else
                        {
                            subtitleUrl = sourceSubtitle != null
                                ? sourceSubtitle.Url
                                : null;
                        }
                    }
                }
            }
            else
                subtitleUrl = null;

            var dash = DashBuilder.GenerateOnDemandDash(sourceVideo, videoUrl, sourceAudio, audioUrl, sourceSubtitle, subtitleUrl);
            var dashTask = Task.FromResult(dash);
            if (!videoIsLocal && !audioIsLocal && !subtitleIsLocal)
                state.DetailsState.SetCachedDash(videoIndex, audioIndex, subtitleIndex, subtitleIsLocal, proxySettings, dashTask);
            return (dashTask, null);
        }

        private static ISubtitleSource SubtitleToProxied(WindowState state, ISubtitleSource sourceSubtitle, bool subtitleIsLocal, int subtitleIndex, ProxySettings? proxySettings, string? modifierId = null)
        {
            if (sourceSubtitle == null)
                return null;

            var subtitleUrl = BuildSubtitleUrl(state, subtitleIndex, subtitleIsLocal, proxySettings, modifierId);

            return new SubtitleSource()
            {
                Name = sourceSubtitle.Name,
                Language = sourceSubtitle.Language,
                HasFetch = false,
                Format = sourceSubtitle.Format,
                Url = subtitleUrl
            };
        }

        public static Task<string> GenerateSourceDashRaw(WindowState state, DashManifestRawSource videoSource, DashManifestRawAudioSource audioSource, ISubtitleSource subtitleSource, ProxySettings? proxySettings, out V8PromiseMetadata promiseMeta)
        {
            var merging = (audioSource == null) ? videoSource : new DashManifestMergingRawSource(videoSource, audioSource, subtitleSource);

            var dashTask = merging.GenerateAsync(out promiseMeta);

            return dashTask.ContinueWith((t) =>
            {
                var dash = dashTask.Result;
                var oldVReqEx = state.DetailsState._videoRequestExecutor;
                var oldAReqEx = state.DetailsState._audioRequestExecutor;
                oldVReqEx?.Cleanup();
                state.DetailsState._videoRequestExecutor = null;
                oldAReqEx?.Cleanup();
                state.DetailsState._audioRequestExecutor = null;

                string videoUrl = null;
                string audioUrl = null;

                if (merging is DashManifestMergingRawSource mergingM)
                {
                    if (mergingM.Video.HasRequestExecutor)
                        videoUrl = getRequestExecutorProxy("https://grayjay.app/internal/video", mergingM.Video.GetRequestExecutor(), proxySettings);
                    else
                        throw new NotImplementedException();
                    if (mergingM.Audio.HasRequestExecutor)
                        audioUrl = getRequestExecutorProxy("https://grayjay.app/internal/audio", mergingM.Audio.GetRequestExecutor(), proxySettings);
                    else
                        throw new NotImplementedException();
                }
                else
                {
                    if(merging.HasRequestExecutor)
                    {
                        videoUrl = getRequestExecutorProxy("https://grayjay.app/internal/video", merging.GetRequestExecutor(), proxySettings);
                        audioUrl = videoUrl;
                    }
                }


                foreach (Match representation in DashBuilder.REGEX_REPRESENTATION.Matches(dash))
                {
                    var mediaType = representation.Groups[1].Value ?? throw new InvalidDataException("Media type not found for dash representation");
                    dash = DashBuilder.REGEX_MEDIA_INITIALIZATION.Replace(dash, new MatchEvaluator((m) =>
                    {
                        if (m.Index < representation.Index || (m.Index + m.Length) > (representation.Index + representation.Length))
                            return m.Value;

                        if (mediaType.StartsWith("video/"))
                            return $"{m.Groups[1].Value}=\"{videoUrl}?url={HttpUtility.UrlEncode(m.Groups[2].Value).Replace("%24Number%24", "$Number$")}&amp;mediaType={HttpUtility.UrlEncode(mediaType)}\"";
                        else if (mediaType.StartsWith("audio/"))
                            return $"{m.Groups[1].Value}=\"{audioUrl}?url={HttpUtility.UrlEncode(m.Groups[2].Value).Replace("%24Number%24", "$Number$")}&amp;mediaType={HttpUtility.UrlEncode(mediaType)}\"";
                        else
                            throw new InvalidDataException("Expected video or audio? got: " + mediaType);
                    }));
                }

                var subUrl = subtitleSource?.Url;
                if (!string.IsNullOrWhiteSpace(subUrl))
                    dash = InjectDashSubtitle(dash, subUrl + "&asVtt=true", SubtitleLanguage.Resolve(subtitleSource?.Language, subtitleSource?.Name), subtitleSource?.Name);

                return dash;
            });
        }

        private static string getRequestExecutorProxy(string registerUrl, RequestExecutor reqExecutor, ProxySettings? proxySettings)
        {
            if (reqExecutor != null && !reqExecutor.DidCleanup)
                return HttpProxy.Get(proxySettings?.IsLoopback ?? true).Add(new HttpProxyRegistryEntry()
                {
                    Url = "https://internal.grayjay.app/",
                    IsRelative = true,
                    RequestExecutor = (req) =>
                    {
                        var queryParams = HttpUtility.ParseQueryString((req.Path.Contains("?")) ? req.Path.Substring(req.Path.IndexOf("?")) : "");
                        string mediaType = HttpUtility.UrlDecode(queryParams["mediaType"]);
                        if (queryParams["url"] != null)
                        {
                            string url = HttpUtility.UrlDecode(queryParams["url"]);
                            if (reqExecutor.DidCleanup)
                                return null;
                            var result = reqExecutor.ExecuteRequest(url, new Dictionary<string, string>());
                            return new HttpProxyResponse()
                            {
                                StatusCode = 200,
                                Headers = new Engine.Models.HttpHeaders(new Dictionary<string, string>()
                                {
                                    { "Content-Type", mediaType },
                                    { "Content-Length", result.Length.ToString() }
                                }),
                                Version = "HTTP/1.1",
                                Data = result
                            };
                        }
                        else
                            return null;
                    },
                    ResponseHeaderOptions = new ResponseHeaderOptions()
                    {
                        InjectPermissiveCORS = true
                    }
                }, proxySettings?.ProxyAddress);
            else
                throw new NotImplementedException();
        }

        [HttpGet]
        public async Task<IActionResult> SourceDashUrl(int videoIndex, int subtitleIndex = -1, bool subtitleIsLocal = false, bool isLoopback = true)
            => await SourceDashUrlInternal(videoIndex, subtitleIndex, subtitleIsLocal, isLoopback, retried: false);

        private async Task<IActionResult> SourceDashUrlInternal(int videoIndex, int subtitleIndex, bool subtitleIsLocal, bool isLoopback, bool retried)
        {
            var state = this.State();
            var proxySettings = new ProxySettings(isLoopback);
            try
            {
                return Content(await GetOrGenerateSourceDashUrl(state, videoIndex, subtitleIndex, subtitleIsLocal, proxySettings), "application/dash+xml");
            }
            catch (SupersededDashRequestException)
            {
                return StatusCode(409, "The video changed while the DASH manifest was generated");
            }
            catch (ScriptReloadRequiredException reloadEx)
            {
                if (retried)
                    throw;
                await StatePlatform.HandleReloadRequired(reloadEx);
                this.VideoLoad(state.DetailsState.VideoLoaded.Url);
                var reloadedSources = state.DetailsState.VideoLoaded?.Video?.VideoSources;
                if (videoIndex >= 0 && (reloadedSources == null || videoIndex >= reloadedSources.Length))
                    throw new InvalidDataException("Video source is no longer available after reload");
                return await SourceDashUrlInternal(videoIndex, subtitleIndex, subtitleIsLocal, isLoopback, retried: true);
            }
        }

        public static async Task<string> GetOrGenerateSourceDashUrl(WindowState state, int videoIndex, int subtitleIndex, bool subtitleIsLocal, ProxySettings proxySettings)
        {
            var generation = state.DetailsState.CachedDashGeneration;
            var cachedTask = state.DetailsState.GetCachedDashTask(videoIndex, -1, subtitleIndex, subtitleIsLocal, proxySettings);
            if (cachedTask != null)
                return await cachedTask;

            (var mpd, var isDynamic) = GenerateSourceDashUrl(state, generation, videoIndex, subtitleIndex, subtitleIsLocal, proxySettings);
            if (!isDynamic && !state.DetailsState.TrySetCachedDash(generation, videoIndex, -1, subtitleIndex, subtitleIsLocal, proxySettings, Task.FromResult(mpd)))
                throw new SupersededDashRequestException();
            return mpd;
        }

        public static (string Mpd, bool IsDynamic) GenerateSourceDashUrl(WindowState state, long generation, int videoIndex, int subtitleIndex, bool subtitleIsLocal, ProxySettings? proxySettings)
        {
            (var sourceVideo, _, var sourceSubtitle) = GetSources(state, videoIndex, -1, subtitleIndex, false, false, subtitleIsLocal);
            if (!(sourceVideo is DashManifestSource dashSource))
                throw new Exception("Expected a DASH manifest source.");

            var modifier = dashSource.GetRequestModifier();
            var session = state.DetailsState.GetOrCreateDashSourceSession(dashSource);
            var manifestUrl = state.DetailsState.GetDashManifestLocation(dashSource) ?? dashSource.Url;
            var res = ModifierHttp.GetBytes(session.Client, manifestUrl, modifier, new Grayjay.Engine.Models.HttpHeaders(), decodeContent: true);
            if (!res.IsOk)
                throw new InvalidDataException($"Failed to fetch manifest [{res.Code}]");

            var finalUrl = res.FinalUrl;
            var baseUri = GetBaseUri(state, proxySettings);
            var modifierId = modifier != null ? ProxyController.GetOrCreateModifierId(state, modifier, finalUrl) : null;
            if (modifierId != null)
                DetailsState.Modifiers[modifierId] = modifier!;

            var document = XDocument.Load(new MemoryStream(res.Bytes));
            var root = document.Root ?? throw new InvalidDataException("Invalid DASH manifest");
            bool isDynamic = string.Equals((string?)root.Attribute("type"), "dynamic", StringComparison.OrdinalIgnoreCase);

            ResolveDashXlinks(document, new Uri(finalUrl), url =>
            {
                try
                {
                    var xlinkResponse = ModifierHttp.GetBytes(session.Client, url, modifier, new Grayjay.Engine.Models.HttpHeaders(), decodeContent: true);
                    return xlinkResponse.IsOk ? xlinkResponse.Bytes : null;
                }
                catch (Exception e) when (e is not ScriptReloadRequiredException)
                {
                    Logger.w<DetailsController>($"Failed to fetch a DASH xlink document, keeping the original element: {e.Message}");
                    return null;
                }
            });

            string ProxyRootFor(string hostRoot)
            {
                var token = state.DetailsState.TryCreateDashRelativeProxy(generation, () => ProxyController.GetOrCreateDashRelativeProxy(state, hostRoot, modifier, modifierId, session))
                    ?? throw new SupersededDashRequestException();
                return $"{baseUri}/proxy/DashRelative/{token}/";
            }

            var location = RewriteDashManifestForProxy(document, new Uri(finalUrl), ProxyRootFor);
            if (location != null)
                state.DetailsState.SetDashManifestLocation(dashSource, location);

            if (sourceSubtitle != null)
                InjectDashSubtitleIntoDocument(document, BuildSubtitleUrl(state, subtitleIndex, subtitleIsLocal, proxySettings, modifierId) + "&asVtt=true", SubtitleLanguage.Resolve(sourceSubtitle.Language, sourceSubtitle.Name), sourceSubtitle.Name);
            return (document.ToString(SaveOptions.DisableFormatting), isDynamic);
        }

        private const string DashProxyProbeRoot = "http://proxy.invalid/root/";
        private static readonly Regex DashTemplateIdentifierRegex = new Regex(@"\$[^$]*\$", RegexOptions.Compiled);
        private static readonly Regex DashUrlSchemeRegex = new Regex("^[A-Za-z][A-Za-z0-9+.-]*:", RegexOptions.Compiled);
        private static readonly (string Element, string Attribute)[] DashUrlAttributes = new (string Element, string Attribute)[]
        {
            ("SegmentTemplate", "media"),
            ("SegmentTemplate", "initialization"),
            ("SegmentTemplate", "index"),
            ("SegmentTemplate", "bitstreamSwitching"),
            ("SegmentURL", "media"),
            ("SegmentURL", "index"),
            ("Initialization", "sourceURL"),
            ("RepresentationIndex", "sourceURL"),
            ("BitstreamSwitching", "sourceURL")
        };
        private static readonly string[] DashSegmentElementNames = new[] { "SegmentBase", "SegmentList", "SegmentTemplate" };
        private static readonly string[] DashUrlChildElementNames = new[] { "Initialization", "RepresentationIndex", "BitstreamSwitching", "SegmentURL" };
        private static readonly HashSet<string> DashHttpTimingSchemes = new HashSet<string>(StringComparer.Ordinal)
        {
            "urn:mpeg:dash:utc:http-head:2014", "urn:mpeg:dash:utc:http-xsdate:2014", "urn:mpeg:dash:utc:http-iso:2014", "urn:mpeg:dash:utc:http-ntp:2014",
            "urn:mpeg:dash:utc:http-head:2012", "urn:mpeg:dash:utc:http-xsdate:2012", "urn:mpeg:dash:utc:http-iso:2012", "urn:mpeg:dash:utc:http-ntp:2012"
        };
        private static readonly XNamespace XlinkNamespace = "http://www.w3.org/1999/xlink";
        private const string DashResolveToZero = "urn:mpeg:dash:resolve-to-zero:2013";

        /// <summary>
        /// Replaces Period, then AdaptationSet and EventStream elements with xlink:actuate="onLoad" by the remote elements, like dash.js.
        /// A failed fetch keeps the element without its xlink attributes. Other xlinks and resolve-to-zero are left to the player.
        /// </summary>
        /// <param name="fetch">Returns the body of an absolute http(s) URL, or null on failure.</param>
        public static void ResolveDashXlinks(XDocument document, Uri manifestUri, Func<string, byte[]?> fetch)
        {
            var root = document.Root ?? throw new InvalidDataException("Invalid DASH manifest");
            ResolveDashXlinkElements(root.Elements().Where(element => element.Name.LocalName == "Period").ToList(), root, manifestUri, fetch);
            var periodChildren = root.Elements().Where(element => element.Name.LocalName == "Period")
                .SelectMany(period => period.Elements().Where(element => element.Name.LocalName == "AdaptationSet" || element.Name.LocalName == "EventStream"))
                .ToList();
            ResolveDashXlinkElements(periodChildren, root, manifestUri, fetch);
        }

        private static void ResolveDashXlinkElements(List<XElement> elements, XElement root, Uri manifestUri, Func<string, byte[]?> fetch)
        {
            foreach (var element in elements)
            {
                var href = ((string?)element.Attribute(XlinkNamespace + "href"))?.Trim();
                if (string.IsNullOrEmpty(href) || href == DashResolveToZero || (string?)element.Attribute(XlinkNamespace + "actuate") != "onLoad")
                    continue;

                List<XElement>? resolved = null;
                if (Uri.TryCreate(manifestUri, href, out var hrefUri) && IsHttpUri(hrefUri))
                {
                    var body = fetch(hrefUri.AbsoluteUri);
                    if (body != null)
                        resolved = ParseDashXlinkResponse(body, root, element.Name);
                }

                if (resolved == null || resolved.Count == 0)
                {
                    element.Attribute(XlinkNamespace + "href")?.Remove();
                    element.Attribute(XlinkNamespace + "actuate")?.Remove();
                    continue;
                }
                element.ReplaceWith(resolved);
            }
        }

        /// <summary>
        /// Parses a remote xlink document, which may hold several sibling elements, and returns the elements named like the target.
        /// The reader detects the encoding from the BOM or XML declaration; the MPD root's namespaces are in scope for unprefixed elements.
        /// </summary>
        private static List<XElement>? ParseDashXlinkResponse(byte[] body, XElement root, XName targetName)
        {
            var namespaceManager = new System.Xml.XmlNamespaceManager(new System.Xml.NameTable());
            foreach (var attribute in root.Attributes().Where(attribute => attribute.IsNamespaceDeclaration))
                namespaceManager.AddNamespace(attribute.Name.Namespace == XNamespace.None ? "" : attribute.Name.LocalName, attribute.Value);
            var parserContext = new System.Xml.XmlParserContext(null, namespaceManager, null, System.Xml.XmlSpace.None);
            var readerSettings = new System.Xml.XmlReaderSettings { ConformanceLevel = System.Xml.ConformanceLevel.Fragment, DtdProcessing = System.Xml.DtdProcessing.Prohibit };

            var parsed = new List<XElement>();
            try
            {
                using var reader = System.Xml.XmlReader.Create(new MemoryStream(body), readerSettings, parserContext);
                reader.Read();
                while (!reader.EOF)
                {
                    if (reader.NodeType == System.Xml.XmlNodeType.Element)
                    {
                        parsed.Add((XElement)XNode.ReadFrom(reader));
                    }
                    else
                    {
                        reader.Read();
                    }
                }
            }
            catch (System.Xml.XmlException e)
            {
                Logger.w<DetailsController>($"Failed to parse a DASH xlink document: {e.Message}");
                return null;
            }

            var resolved = parsed.Where(element => element.Name.LocalName == targetName.LocalName).ToList();
            foreach (var element in resolved)
            {
                element.Attribute(XlinkNamespace + "href")?.Remove();
                element.Attribute(XlinkNamespace + "actuate")?.Remove();
                var sourceNamespace = element.Name.Namespace;
                if (sourceNamespace == targetName.Namespace)
                    continue;
                foreach (var descendant in element.DescendantsAndSelf().Where(descendant => descendant.Name.Namespace == sourceNamespace))
                {
                    descendant.Name = targetName.Namespace + descendant.Name.LocalName;
                    descendant.Attributes().Where(attribute => attribute.IsNamespaceDeclaration && attribute.Name.Namespace == XNamespace.None).Remove();
                }
            }
            return resolved;
        }

        /// <summary>
        /// Routes every BaseURL and URL attribute of an MPD through the DashRelative proxy, resolved per level like ExoPlayer.
        /// Relative values that stay under the proxy root for every parent BaseURL are kept relative, so BaseURL failover still works.
        /// Inherited segment URLs are first copied onto Representations with their own BaseURL, since players resolve them against that base.
        /// Removes Location and PatchLocation and returns the resolved Location URL, or null.
        /// </summary>
        /// <param name="proxyRootFor">Maps an upstream host root (scheme://authority/) to its proxy root URL ending in '/'.</param>
        public static string? RewriteDashManifestForProxy(XDocument document, Uri manifestUri, Func<string, string> proxyRootFor)
        {
            var root = document.Root ?? throw new InvalidDataException("Invalid DASH manifest");
            XNamespace ns = root.Name.Namespace;

            string? location = null;
            foreach (var locationElement in root.Elements(ns + "Location").ToList())
            {
                if (location == null && Uri.TryCreate(manifestUri, locationElement.Value.Trim(), out var resolvedLocation) && IsHttpUri(resolvedLocation))
                {
                    location = resolvedLocation.AbsoluteUri;
                }
                locationElement.Remove();
            }
            root.Elements(ns + "PatchLocation").Remove();

            if (!root.Elements(ns + "BaseURL").Any())
                root.AddFirst(new XElement(ns + "BaseURL", manifestUri.AbsoluteUri));

            InlineDashMpdUrlQuery(root, manifestUri);
            PushDownInheritedDashSegmentUrls(root);
            // dash.js resolves MPD-level relative BaseURLs against the served manifest URL, so they are always made absolute.
            RewriteDashElementForProxy(root, new[] { manifestUri }, false, proxyRootFor);
            return location;
        }

        /// <summary>
        /// The player would append the query of the served (local) manifest URL for useMPDUrlQuery="true",
        /// so the upstream manifest query is appended to queryString instead, in the order dash.js uses, and the flag is cleared.
        /// </summary>
        private static void InlineDashMpdUrlQuery(XElement root, Uri manifestUri)
        {
            var manifestUrl = manifestUri.OriginalString;
            var queryStart = manifestUrl.IndexOf('?');
            var fragmentStart = manifestUrl.IndexOf('#');
            var upstreamQuery = "";
            if (queryStart >= 0 && (fragmentStart < 0 || queryStart < fragmentStart))
            {
                var queryEnd = fragmentStart > queryStart ? fragmentStart : manifestUrl.Length;
                upstreamQuery = manifestUrl.Substring(queryStart + 1, queryEnd - queryStart - 1);
            }

            foreach (var queryInfo in root.Descendants().Where(element => element.Name.LocalName is "UrlQueryInfo" or "ExtUrlQueryInfo"))
            {
                var useMpdUrlQuery = queryInfo.Attribute("useMPDUrlQuery");
                if (useMpdUrlQuery == null || useMpdUrlQuery.Value != "true")
                    continue;

                useMpdUrlQuery.Value = "false";
                if (upstreamQuery.Length == 0)
                    continue;

                var queryString = (string?)queryInfo.Attribute("queryString");
                queryInfo.SetAttributeValue("queryString", string.IsNullOrEmpty(queryString) ? upstreamQuery : queryString + "&" + upstreamQuery);
            }
        }

        /// <summary>
        /// Copies each segment URL a Representation inherits onto the Representation's own segment element when a BaseURL sits
        /// between the defining level and the Representation. Nearest level wins per attribute or child name, like dash.js.
        /// </summary>
        private static void PushDownInheritedDashSegmentUrls(XElement root)
        {
            XNamespace ns = root.Name.Namespace;
            foreach (var representation in root.Elements(ns + "Period").Elements(ns + "AdaptationSet").Elements(ns + "Representation"))
            {
                var adaptationSet = representation.Parent!;
                var levels = new[] { representation, adaptationSet, adaptationSet.Parent! };
                foreach (var segmentName in DashSegmentElementNames)
                {
                    foreach (var target in DashUrlAttributes)
                    {
                        if (target.Element != segmentName)
                            continue;
                        var sourceIndex = Array.FindIndex(levels, level => level.Element(ns + segmentName)?.Attribute(target.Attribute) != null);
                        if (!HasDashBaseUrlBelow(levels, sourceIndex, ns))
                            continue;
                        var value = levels[sourceIndex].Element(ns + segmentName)!.Attribute(target.Attribute)!.Value;
                        GetOrAddDashSegmentElement(representation, ns + segmentName).SetAttributeValue(target.Attribute, value);
                    }

                    foreach (var childName in DashUrlChildElementNames)
                    {
                        var sourceIndex = Array.FindIndex(levels, level => level.Element(ns + segmentName)?.Element(ns + childName) != null);
                        if (!HasDashBaseUrlBelow(levels, sourceIndex, ns))
                            continue;
                        var children = levels[sourceIndex].Element(ns + segmentName)!.Elements(ns + childName).Select(child => new XElement(child)).ToList();
                        GetOrAddDashSegmentElement(representation, ns + segmentName).Add(children);
                    }
                }
            }
        }

        private static bool HasDashBaseUrlBelow(XElement[] levels, int sourceIndex, XNamespace ns)
        {
            return sourceIndex > 0 && levels.Take(sourceIndex).Any(level => level.Element(ns + "BaseURL") != null);
        }

        private static XElement GetOrAddDashSegmentElement(XElement representation, XName segmentName)
        {
            var segmentElement = representation.Element(segmentName);
            if (segmentElement == null)
            {
                segmentElement = new XElement(segmentName);
                representation.Add(segmentElement);
            }
            return segmentElement;
        }

        /// <summary>
        /// Rewrites one MPD level. <paramref name="parentBases"/> are the upstream BaseURL candidates dash.js may select for the parent level.
        /// </summary>
        private static void RewriteDashElementForProxy(XElement element, IReadOnlyList<Uri> parentBases, bool keepRelative, Func<string, string> proxyRootFor)
        {
            var baseUrlName = element.Name.Namespace + "BaseURL";
            var levelBases = new List<Uri>();
            foreach (var baseUrlElement in element.Elements(baseUrlName))
            {
                var value = baseUrlElement.Value.Trim();
                if (keepRelative && IsRelativeDashPath(value) && parentBases.All(parentBase => StaysUnderProxyRoot(value, parentBase)))
                {
                    foreach (var parentBase in parentBases)
                    {
                        if (Uri.TryCreate(parentBase, value, out var relativeResolved))
                            levelBases.Add(relativeResolved);
                    }
                    continue;
                }

                if (!Uri.TryCreate(parentBases[0], value, out var resolved) || !IsHttpUri(resolved))
                    continue;
                baseUrlElement.Value = ToDashProxyUrl(resolved.AbsoluteUri, proxyRootFor);
                levelBases.Add(resolved);
            }
            IReadOnlyList<Uri> bases = levelBases.Count > 0 ? levelBases : parentBases;

            // dash.js resolves relative clock URLs against the MPD BaseURL, which is the proxy, so every URL is made absolute upstream.
            if (element.Name.LocalName == "UTCTiming" && DashHttpTimingSchemes.Contains(((string?)element.Attribute("schemeIdUri"))?.Trim() ?? ""))
            {
                var valueAttribute = element.Attribute("value");
                if (valueAttribute != null)
                {
                    valueAttribute.Value = string.Join(" ", valueAttribute.Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                        .Select(clockUrl => Uri.TryCreate(bases[0], clockUrl, out var resolvedClock) && IsHttpUri(resolvedClock) ? ToDashProxyUrl(resolvedClock.AbsoluteUri, proxyRootFor) : clockUrl));
                }
            }

            foreach (var target in DashUrlAttributes)
            {
                if (element.Name.LocalName != target.Element)
                    continue;
                var attribute = element.Attribute(target.Attribute);
                if (attribute == null)
                    continue;

                // Values may hold $Number%05d$ templates, so they are resolved as strings rather than parsed as Uri.
                var absoluteUrl = ToAbsoluteDashTemplateUrl(attribute.Value, bases[0]);
                if (absoluteUrl == null && IsRelativeDashPath(attribute.Value) && !bases.All(levelBase => StaysUnderProxyRoot(attribute.Value, levelBase)))
                    absoluteUrl = ResolveDashTemplateUrl(bases[0], attribute.Value);
                if (absoluteUrl != null)
                    attribute.Value = ToDashProxyUrl(absoluteUrl, proxyRootFor);
            }

            foreach (var child in element.Elements())
            {
                if (child.Name != baseUrlName)
                    RewriteDashElementForProxy(child, bases, true, proxyRootFor);
            }
        }

        private static bool IsRelativeDashPath(string value)
        {
            return value.Length > 0 && !value.StartsWith("/", StringComparison.Ordinal) && !DashUrlSchemeRegex.IsMatch(value);
        }

        /// <summary>
        /// Whether a relative value resolves to the same path under the proxy root as upstream; climbing above the upstream root would leave the proxy prefix.
        /// </summary>
        private static bool StaysUnderProxyRoot(string relative, Uri upstreamBase)
        {
            var placeholderValue = ReplaceDashTemplates(relative, out _);
            var probeBase = new Uri(DashProxyProbeRoot + upstreamBase.PathAndQuery.TrimStart('/'));
            if (!Uri.TryCreate(upstreamBase, placeholderValue, out var upstreamResolved) || !Uri.TryCreate(probeBase, placeholderValue, out var probeResolved))
                return false;
            return probeResolved.PathAndQuery == "/root" + upstreamResolved.PathAndQuery;
        }

        private static string? ResolveDashTemplateUrl(Uri levelBase, string relative)
        {
            var placeholderValue = ReplaceDashTemplates(relative, out var identifiers);
            if (!Uri.TryCreate(levelBase, placeholderValue, out var resolved) || !IsHttpUri(resolved))
                return null;
            var absoluteUrl = resolved.AbsoluteUri;
            for (var index = 0; index < identifiers.Count; index++)
                absoluteUrl = absoluteUrl.Replace(DashTemplatePlaceholder(index), identifiers[index]);
            return absoluteUrl;
        }

        private static string ReplaceDashTemplates(string value, out List<string> identifiers)
        {
            var found = new List<string>();
            var replaced = DashTemplateIdentifierRegex.Replace(value, match =>
            {
                found.Add(match.Value);
                return DashTemplatePlaceholder(found.Count - 1);
            });
            identifiers = found;
            return replaced;
        }

        private static string DashTemplatePlaceholder(int index) => "grayjaydashtemplate" + index + "x";

        private static string? ToAbsoluteDashTemplateUrl(string value, Uri levelBase)
        {
            if (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                return value;
            if (value.StartsWith("//", StringComparison.Ordinal))
                return levelBase.Scheme + ":" + value;
            if (value.StartsWith("/", StringComparison.Ordinal))
                return levelBase.GetLeftPart(UriPartial.Authority) + value;
            return null;
        }

        private static string ToDashProxyUrl(string absoluteUrl, Func<string, string> proxyRootFor)
        {
            var authorityStart = absoluteUrl.IndexOf("://", StringComparison.Ordinal) + 3;
            var pathStart = absoluteUrl.IndexOfAny(new[] { '/', '?', '#' }, authorityStart);
            if (pathStart < 0)
                return proxyRootFor(absoluteUrl + "/");
            if (absoluteUrl[pathStart] == '/')
            {
                // Dot segments are removed upstream-side first; left in place, the browser would climb out of the proxy prefix.
                var pathEnd = absoluteUrl.IndexOfAny(new[] { '?', '#' }, pathStart);
                var path = RemoveDashDotSegments(pathEnd < 0 ? absoluteUrl[pathStart..] : absoluteUrl[pathStart..pathEnd]);
                var rest = pathEnd < 0 ? "" : absoluteUrl[pathEnd..];
                return proxyRootFor(absoluteUrl[..(pathStart + 1)]) + path[1..] + rest;
            }
            return proxyRootFor(absoluteUrl[..pathStart] + "/") + absoluteUrl[pathStart..];
        }

        /// <summary>
        /// Removes "." and ".." segments from an absolute path like a browser, including percent-encoded dots.
        /// Other characters, escapes, DASH $identifiers$ and repeated slashes are kept as they are.
        /// </summary>
        public static string RemoveDashDotSegments(string path)
        {
            var segments = path.Split('/');
            var output = new List<string>();
            for (var index = 1; index < segments.Length; index++)
            {
                var segment = segments[index];
                var isLast = index == segments.Length - 1;
                var normalizedSegment = segment.Replace("%2e", ".", StringComparison.OrdinalIgnoreCase);
                if (normalizedSegment == "..")
                {
                    if (output.Count > 0)
                        output.RemoveAt(output.Count - 1);
                    if (isLast)
                        output.Add("");
                }
                else if (normalizedSegment == ".")
                {
                    if (isLast)
                        output.Add("");
                }
                else
                {
                    output.Add(segment);
                }
            }
            return "/" + string.Join("/", output);
        }

        private static bool IsHttpUri(Uri uri) => uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps;

        [HttpGet]
        public async Task<IActionResult> SourceHLS(int videoIndex = -1, int audioIndex = -1, int subtitleIndex = -1, bool subtitleIsLocal = false, bool isLoopback = true, string? modifierId = null)
        {
            return Content(await GenerateSourceHLS(this.State(), videoIndex, audioIndex, subtitleIndex, subtitleIsLocal, new ProxySettings(isLoopback), modifierId), "application/x-mpegurl");
        }

        public static async Task<string> GenerateSourceHLS(WindowState state, int videoManifest, int audioManifest, int subtitleIndex, bool subtitleIsLocal, ProxySettings? proxySettings = null, string? modifierId = null)
        {
            if (videoManifest < 0 && audioManifest < 0 && videoManifest != -999)
                throw new Exception("No manifest index provided");

            (var sourceVideo, var sourceAudio, _) = GetSources(state, videoManifest, audioManifest, -1, false, false, false);

            string hlsUrl;
            IRequestModifier? modifier = null;

            if (sourceVideo is HLSManifestSource hv)
            {
                hlsUrl = hv.Url;
                modifier = hv.GetRequestModifier();
            }
            else if (sourceVideo is HLSVariantVideoUrlSource variantVideo)
            {
                hlsUrl = variantVideo.Url;
                modifier = variantVideo.GetRequestModifier();
            }
            else if (sourceAudio is HLSVariantAudioUrlSource variantAudio)
            {
                hlsUrl = variantAudio.Url;
                modifier = variantAudio.GetRequestModifier();
            }
            else if (sourceAudio is HLSManifestAudioSource ha)
            {
                hlsUrl = ha.Url;
                modifier = ha.GetRequestModifier();
            }
            else
                throw new Exception("Expected HLS video or audio manifest source.");

            if (sourceVideo != null && sourceAudio != null && sourceAudio is not HLSManifestAudioSource && sourceAudio is not HLSVariantAudioUrlSource)
                throw new NotSupportedException("Combining HLS video with a non-HLS audio stream requires a media conversion.");
            if (subtitleIndex >= 0 && EnsureVideo(state).IsLive)
                throw new NotSupportedException("External live captions require receiver subtitle support; a static subtitle playlist cannot represent ongoing live captions.");

            var headers = new Grayjay.Engine.Models.HttpHeaders();
            var res = ModifierHttp.GetBytes(new ManagedHttpClient(), hlsUrl, modifier, headers);
            if (!res.IsOk)
                throw new InvalidDataException($"Failed to fetch manifest [{res.Code}]");

            var finalUrl = res.FinalUrl;
            var body = Encoding.UTF8.GetString(res.Bytes);
            var baseUri = GetBaseUri(state, proxySettings);
            if (string.IsNullOrEmpty(modifierId) && modifier != null)
                modifierId = ProxyController.GetOrCreateModifierId(state, modifier, finalUrl);

            void AddSelectedAudio(Parsers.HLS.MasterPlaylist master)
            {
                if (sourceVideo == null || sourceAudio == null) return;
                var audioUrl = sourceAudio is HLSManifestAudioSource a ? a.Url : ((HLSVariantAudioUrlSource)sourceAudio).Url;
                var audioModifier = (sourceAudio as JSSource)?.GetRequestModifier();
                var audioModifierId = audioModifier != null ? ProxyController.GetOrCreateModifierId(state, audioModifier, audioUrl) : null;
                var uri = $"{baseUri}/proxy/HLS?url={HttpUtility.UrlEncode(audioUrl)}&proxyMedia=true&windowId={state.WindowID}"
                    + (audioModifierId != null ? $"&modifierId={Uri.EscapeDataString(audioModifierId)}" : "");
                var group = "gj-audio";
                while (master.MediaRenditions.Any(r => r.GroupId == group)) group += "-selected";
                master.MediaRenditions.Add(new Parsers.HLS.MediaRendition("AUDIO", uri, group, sourceAudio.Language, sourceAudio.Name ?? "Audio", true, true, false));
                foreach (var variant in master.VariantPlaylistsRefs) variant.StreamInfo.Audio = group;
            }

            try
            {
                var master = Parsers.HLS.ParseMasterPlaylist(body, finalUrl);
                if (master.VariantPlaylistsRefs.Count == 0) throw new InvalidDataException("Expected an HLS master playlist.");
                ProxyController.ProxyHLSMasterPlaylist(baseUri, master, proxyMedia: true, modifierId: modifierId, windowId: state.WindowID);
                AddSelectedAudio(master);
                if (subtitleIndex < 0)
                    return master.GenerateM3U8();

                var subsGroupId = "gj-subs";
                while (master.MediaRenditions.Any(r => r.GroupId == subsGroupId)) subsGroupId += "-external";
                var subsPlaylist =
                    $"{baseUri}/Details/SubtitleHLS?subtitleIndex={subtitleIndex}"
                    + $"&subtitleIsLocal={subtitleIsLocal}"
                    + $"&windowId={state.WindowID}"
                    + (!string.IsNullOrEmpty(modifierId) ? $"&modifierId={Uri.EscapeDataString(modifierId)}" : "");

                master.MediaRenditions.Add(new Parsers.HLS.MediaRendition(
                    type: "SUBTITLES",
                    uri: subsPlaylist,
                    groupId: subsGroupId,
                    language: null,
                    name: "Subtitles",
                    isDefault: true,
                    isAutoSelect: true,
                    isForced: false
                ));

                foreach (var vref in master.VariantPlaylistsRefs)
                {
                    if (vref.StreamInfo == null)
                        continue;

                    vref.StreamInfo.Subtitles = subsGroupId;
                }

                return master.GenerateM3U8();
            }
            catch (InvalidDataException) when (body.Split('\n').Any(line => line.TrimStart().StartsWith("#EXTINF:")))
            {
                if (subtitleIndex < 0 && sourceAudio == null)
                {
                    return (await ProxyController.GenerateProxiedHLS(finalUrl, proxyMedia: true, baseUri: baseUri, state: state, modifier: modifier, modifierId: modifierId)).GenerateM3U8();
                }

                var subsGroupId = "gj-subs";
                var subsPlaylist =
                    $"{baseUri}/Details/SubtitleHLS?subtitleIndex={subtitleIndex}"
                    + $"&subtitleIsLocal={subtitleIsLocal}"
                    + $"&windowId={state.WindowID}"
                    + (!string.IsNullOrEmpty(modifierId) ? $"&modifierId={Uri.EscapeDataString(modifierId)}" : "");

                var proxiedVariant =
                    $"{baseUri}/proxy/HLS?url={HttpUtility.UrlEncode(finalUrl)}&proxyMedia=true"
                    + (!string.IsNullOrEmpty(modifierId) ? $"&modifierId={Uri.EscapeDataString(modifierId)}" : "")
                    + $"&windowId={state.WindowID}";

                int? bw = null;
                string? reso = null;
                string? codecs = null;

                if (sourceVideo != null)
                {
                    var t = sourceVideo.GetType();
                    if (t.GetProperty("Bitrate")?.GetValue(sourceVideo) is int bwi) bw = bwi;
                    if (t.GetProperty("Codec")?.GetValue(sourceVideo) is string c && !c.Equals("HLS", StringComparison.OrdinalIgnoreCase)) codecs = c;

                    var w = t.GetProperty("Width")?.GetValue(sourceVideo) as int?;
                    var h = t.GetProperty("Height")?.GetValue(sourceVideo) as int?;
                    if (w.HasValue && h.HasValue && w.Value > 0 && h.Value > 0)
                        reso = $"{w.Value}x{h.Value}";
                }

                var wrapped = new Parsers.HLS.MasterPlaylist(
                    version: 3,
                    variantPlaylistsRefs: new List<Parsers.HLS.VariantPlaylistReference>
                    {
                        new Parsers.HLS.VariantPlaylistReference(
                            proxiedVariant,
                            new Parsers.HLS.StreamInfo(
                                bandwidth: bw ?? 1_000_000,
                                resolution: reso,
                                codecs: codecs,
                                frameRate: null,
                                videoRange: null,
                                audio: null,
                                video: null,
                                subtitles: subtitleIndex >= 0 ? subsGroupId : null,
                                closedCaptions: null
                            )
                        )
                    },
                    mediaRenditions: new List<Parsers.HLS.MediaRendition>
                    {
                        new Parsers.HLS.MediaRendition(
                            type: "SUBTITLES",
                            uri: subsPlaylist,
                            groupId: subsGroupId,
                            language: null,
                            name: "Subtitles",
                            isDefault: true,
                            isAutoSelect: true,
                            isForced: false
                        )
                    },
                    sessionDataList: new List<Parsers.HLS.SessionData>(),
                    independentSegments: body.Contains("#EXT-X-INDEPENDENT-SEGMENTS", StringComparison.Ordinal)
                );

                if (subtitleIndex < 0) wrapped.MediaRenditions.Clear();
                AddSelectedAudio(wrapped);
                return wrapped.GenerateM3U8();
            }
        }

        [HttpGet]
        public async Task<IActionResult> SourceAuto(string? url = null)
        {
            var window = this.State();
            EnsureVideoSelection(window, url);
            var state = window.DetailsState;
            if(state.VideoLocal != null)
            {
                var local = EnsureLocal(this.State());
                var bestVideoSourceIndex = VideoHelper.SelectBestVideoSourceIndex(local.VideoSources.Cast<IVideoSource>().ToList(), 9999*9999, new List<string>() { "video/mp4" });
                var bestAudioSourceIndex = VideoHelper.SelectBestAudioSourceIndex(local.AudioSources.Cast<IAudioSource>().ToList(), new List<string>() { "audio/mp4" }, GrayjaySettings.Instance.Playback.GetPrimaryLanguage(), 9999 * 9999);
                var bestSubtitleSourceIndex = local.SubtitleSources.Count > 0 ? 0 : -1;
                return await SourceProxy(bestVideoSourceIndex, bestAudioSourceIndex, bestSubtitleSourceIndex, true, true, true, url: url);
            }
            else
            {
                var video = EnsureVideo(this.State());
                var bestVideoSourceIndex = VideoHelper.SelectBestVideoSourceIndex(video.Video.VideoSources.Cast<IVideoSource>().ToList(), GrayjaySettings.Instance.Playback.GetPreferredQualityPixelCount(), new List<string>() { "video/mp4" });
                var bestAudioSourceIndex = (video.Video is UnMuxedVideoDescriptor unmuxed) ? 
                    VideoHelper.SelectBestAudioSourceIndex(unmuxed.AudioSources.Cast<IAudioSource>().ToList(), new List<string>() { "audio/mp4" }, GrayjaySettings.Instance.Playback.GetPrimaryLanguage(), 9999 * 9999) : 
                    -1;

                if (bestVideoSourceIndex == -1 && bestAudioSourceIndex == -1 && video.DateTime > DateTime.Now)
                    throw new DialogException(new ExceptionModel()
                    {
                        Type = ExceptionModel.EXCEPTION_GENERAL,
                        Title = "Video unavailable",
                        Message = "The video is not yet available, auto-reload is TODO."
                    });

                if (bestVideoSourceIndex == -1 && bestAudioSourceIndex == -1 && video.Live != null)
                    return await SourceProxy(-999, -1, -1, false, false, false, url: url);

                if (bestVideoSourceIndex >= 0 && bestAudioSourceIndex >= 0)
                {
                    (var videoSources, var audioSource, _) = GetSources(this.State(), bestVideoSourceIndex, bestAudioSourceIndex, -1, false, false, false);

                    if(videoSources is DashManifestRawSource && audioSource is DashManifestRawAudioSource)
                    {
                        return await SourceProxy(bestVideoSourceIndex, bestAudioSourceIndex, -1, false, false, false, url: url);
                    }
                    else if (!(videoSources is IStreamMetaDataSource) || !(audioSource is IStreamMetaDataSource))
                        throw DialogException.FromException("Cannot play this source",
                            new Exception("Unmuxed sources require IStreamMetaDataSource info to translate to dash"));
                }
                return await SourceProxy(bestVideoSourceIndex, bestAudioSourceIndex, -1, false, false, false, url: url);
            }
        }

        [HttpGet]
        public async Task<IActionResult> SourceProxy(int videoIndex, int audioIndex, int subtitleIndex, bool videoIsLocal = false, bool audioIsLocal = false, bool subtitleIsLocal = false, string? tag = null, string? url = null)
        {
            var state = this.State();
            var video = EnsureVideoSelection(state, url);
            var local = state.DetailsState.VideoLocal;
            var descriptor = await GenerateSourceProxy(state, videoIndex, audioIndex, subtitleIndex, videoIsLocal, audioIsLocal, subtitleIsLocal, null, tag);
            if (!ReferenceEquals(video, state.DetailsState.VideoLoaded) || !ReferenceEquals(local, state.DetailsState.VideoLocal))
                throw new BadHttpRequestException("Source selection is obsolete", StatusCodes.Status409Conflict);
            return Ok(descriptor);
        }
        public static async Task<SourceDescriptor> GenerateSourceProxy(WindowState state, int videoIndex, int audioIndex, int subtitleIndex, bool videoIsLocal = false, bool audioIsLocal = false, bool subtitleIsLocal = false, ProxySettings? proxySettings = null, string? tag = null, bool forceReady = false)
        {
            var video = EnsureVideo(state);

            if (videoIndex == -999 && video.Live is UMPSource liveUmp && (proxySettings?.IsLoopback ?? true))
                return UmpSourceDescriptor(state, liveUmp, videoIndex, subtitleIndex, subtitleIsLocal, tag);

            if (videoIndex == -999)
            {
                if (video.Live == null)
                    throw new DialogException(new ExceptionModel()
                    {
                        Title = "Livestream not available",
                        Message = "The video's livestream source could not be found",
                        CanRetry = false
                    });
                if (video.Live is HLSManifestSource)
                    return DirectHLSUrlSource(state, videoIndex, audioIndex, subtitleIndex, subtitleIsLocal, proxySettings ?? new ProxySettings(true), null);
                else throw new DialogException(new ExceptionModel()
                {
                    Title = "Livestream type not supported",
                    Message = $"Livestream type not supported [{video.Live.GetType().Name}]",
                    CanRetry = false
                });
            }

            (var sourceVideo, var sourceAudio, var sourceSubtitle) = GetSources(state, videoIndex, audioIndex, subtitleIndex, videoIsLocal, audioIsLocal, subtitleIsLocal);
            if (sourceVideo is UMPSource umpSource)
            {
                if (proxySettings?.IsLoopback ?? true)
                    return UmpSourceDescriptor(state, umpSource, videoIndex, subtitleIndex, subtitleIsLocal, tag);
                throw new InvalidOperationException("UMP sources are cast through UmpCasting, not the source proxy");
            }
            if (sourceVideo is HLSManifestSource || sourceVideo is HLSVariantVideoUrlSource)
                return DirectHLSUrlSource(state, videoIndex, audioIndex, subtitleIndex, subtitleIsLocal, proxySettings ?? new ProxySettings(true), null);

            if (sourceVideo == null && (sourceAudio is HLSManifestAudioSource || sourceAudio is HLSVariantAudioUrlSource))
                return DirectHLSUrlSource(state, -1, audioIndex, subtitleIndex, subtitleIsLocal, proxySettings ?? new ProxySettings(true), null);

            if (sourceVideo is DashManifestSource && sourceAudio == null)
            {
                var dashSubtitleIndex = (sourceSubtitle != null) ? subtitleIndex : -1;
                var subtitleQuery = (dashSubtitleIndex >= 0) ? $"&subtitleIndex={dashSubtitleIndex}&subtitleIsLocal={subtitleIsLocal}" : "";
                return new SourceDescriptor($"/details/SourceDashUrl?videoIndex={videoIndex}{subtitleQuery}&isLoopback={proxySettings?.IsLoopback ?? true}&windowId={state.WindowID}", "application/dash+xml", videoIndex, -1, dashSubtitleIndex, false, false, subtitleIsLocal);
            }

            if (sourceVideo is LocalVideoSource && (sourceAudio is LocalAudioSource || sourceSubtitle != null))
            {
                string manifest;
                if (sourceVideo is LocalVideoSource localVideo && (sourceAudio == null || sourceAudio is LocalAudioSource)
                    && (localVideo.MetaData == null || sourceAudio is LocalAudioSource { MetaData: null }))
                    manifest = await Transcoding.LocalDash.GenerateAsync(state.LocalMedia, localVideo, sourceAudio as LocalAudioSource,
                        GetBaseUri(state, proxySettings), state.WindowID,
                        sourceSubtitle != null ? BuildSubtitleUrl(state, subtitleIndex, subtitleIsLocal, proxySettings) : null,
                        sourceSubtitle?.Format, sourceSubtitle?.Name, sourceSubtitle?.Language);
                else
                {
                    var (manifestTask, _) = GenerateSourceDash(state, videoIndex, audioIndex, subtitleIndex, videoIsLocal, audioIsLocal, subtitleIsLocal, proxySettings);
                    manifest = await manifestTask;
                }
                var id = state.LocalMedia.RegisterManifest(manifest);
                return new SourceDescriptor($"/Details/LocalDash?id={id}&windowId={Uri.EscapeDataString(state.WindowID)}", "application/dash+xml",
                    videoIndex, audioIndex, subtitleIndex, videoIsLocal, audioIsLocal, subtitleIsLocal);
            }

            if (sourceVideo != null && (sourceAudio != null || sourceSubtitle != null))
            {
                if (sourceAudio != null && !(sourceVideo is DashManifestRawSource && sourceAudio is DashManifestRawAudioSource) && (!(sourceVideo is IStreamMetaDataSource) || !(sourceAudio is IStreamMetaDataSource)))
                    throw DialogException.FromException("Cannot play this source", new Exception("Unmuxed sources require IStreamMetaDataSource info to translate to dash"));

                if (forceReady)
                {
                    //Preload the DASH
                    (var taskGenerateSourceDash, var promiseMetadata) = GenerateSourceDash(state, videoIndex, audioIndex, subtitleIndex, videoIsLocal, audioIsLocal, subtitleIsLocal, proxySettings);
                    if (!taskGenerateSourceDash.IsCompleted && promiseMetadata != null)
                        StateWebsocket.VideoLoader("", promiseMetadata.EstimateDuration, state.WindowID, tag);
                    await taskGenerateSourceDash;
                    StateWebsocket.VideoLoaderFinish(state.WindowID, tag);
                }

                return new SourceDescriptor($"/details/SourceDash?videoIndex={videoIndex}&audioIndex={audioIndex}&subtitleIndex={subtitleIndex}&videoIsLocal={videoIsLocal}&audioIsLocal={audioIsLocal}&subtitleIsLocal={subtitleIsLocal}&isLoopback={proxySettings?.IsLoopback ?? true}&windowId={state.WindowID}&tag={tag}", "application/dash+xml", videoIndex, audioIndex, subtitleIndex, videoIsLocal, audioIsLocal, subtitleIsLocal);
            }
            else if (sourceVideo != null)
            {
                if (sourceVideo is VideoUrlSource vus)
                    return DirectVideoUrlSource(vus, videoIndex, videoIsLocal, proxySettings);
                else if (sourceVideo is HLSManifestSource)
                    return DirectHLSUrlSource(state, videoIndex, audioIndex, subtitleIndex, subtitleIsLocal, proxySettings ?? new ProxySettings(true), null);
                else if (sourceVideo is LocalVideoSource lvs)
                    return LocalVideoSource(state, lvs);
                else if (sourceVideo is DashManifestRawSource das)
                {
                    if (!(sourceVideo is IStreamMetaDataSource))
                        throw DialogException.FromException("Source doesn't provide enough playback info for unmuxed playback (IStreamMetaData)",
                            new Exception("Unmuxed sources require IStreamMetaDataSource info to translate to dash"));

                    if (forceReady)
                    {
                        //Preload the DASH
                        (var taskGenerateSourceDash, var promiseMetadata) = GenerateSourceDash(state, videoIndex, audioIndex, subtitleIndex, videoIsLocal, audioIsLocal, subtitleIsLocal, proxySettings);
                        if (!taskGenerateSourceDash.IsCompleted && promiseMetadata != null)
                            StateWebsocket.VideoLoader("", promiseMetadata.EstimateDuration, state.WindowID, tag);
                        await taskGenerateSourceDash;
                        StateWebsocket.VideoLoaderFinish(state.WindowID, tag);
                    }

                    return new SourceDescriptor($"/details/SourceDash?videoIndex={videoIndex}&audioIndex={audioIndex}&subtitleIndex={subtitleIndex}&videoIsLocal={videoIsLocal}&audioIsLocal={audioIsLocal}&subtitleIsLocal={subtitleIsLocal}&isLoopback={proxySettings?.IsLoopback ?? true}&windowId={state.WindowID}&tag={tag}", "application/dash+xml", videoIndex, audioIndex, subtitleIndex, videoIsLocal, audioIsLocal, subtitleIsLocal);

                }
                else
                    throw new Exception($"Not implemented type {sourceVideo.GetType().Name}");
            }
            else if (sourceAudio != null)
            {
                if (sourceAudio is AudioUrlSource aus)
                    return DirectAudioUrlSource(aus, audioIndex, audioIsLocal, proxySettings);
                else if (sourceAudio is HLSManifestAudioSource)
                    return DirectHLSUrlSource(state, -1, audioIndex, subtitleIndex, subtitleIsLocal, proxySettings ?? new ProxySettings(true), null);
                else if (sourceAudio is LocalAudioSource las)
                    return LocalAudioSource(state, las);
                else
                    throw new Exception($"Not implemented type {sourceAudio.GetType().Name}");
            }
            else
                throw new DialogException(new ExceptionModel()
                {
                    Title = "No sources available on this video",
                    Message = $"Missing video and/or audio stream for [{video?.Name}]\nLivestreams sometimes take some time to become available after it finishes.",
                    CanRetry = false
                });
            //throw new Exception("Select either a videoIndex or audioIndex");
        }

        public static SourceDescriptor UmpSourceDescriptor(WindowState state, UMPSource source, int videoIndex, int subtitleIndex, bool subtitleIsLocal, string? tag)
        {
            var details = state.DetailsState;
            var subtitleUrl = subtitleIndex >= 0 ? BuildSubtitleUrl(state, subtitleIndex, subtitleIsLocal, null) : null;
            var previous = details.UmpPlaybackId != null ? UmpPlaybackRegistry.Get(details.UmpPlaybackId) : null;
            var continued = previous != null && previous.Source.VideoId == source.VideoId && previous.Source.Url == source.Url && previous.Session.FatalError == null && !previous.Session.IsReleased
                ? previous.Session.ExportTransferable() : null;
            details.ReleaseUmpPlayback();
            var playback = UmpPlaybackRegistry.Create(state.WindowID, source, continued == null ? Sabr.Cast.UmpCasting.TakeHandBackState(source.VideoId ?? "") : null, continued);
            playback.Tag = tag;
            playback.SubtitleUrl = subtitleUrl;
            details.UmpPlaybackId = playback.Id;
            return new SourceDescriptor($"/Ump/Info?id={playback.Id}", UMPSource.CONTAINER, videoIndex, -1, subtitleIndex, false, false, subtitleIsLocal);
        }

        private static DialogException CreateCaptchaDialogException(string contentKind, ScriptCaptchaRequiredException captchaException)
        {
            _ = StateApp.HandleCaptchaException(captchaException.Config, captchaException);
            return new DialogException(new ExceptionModel()
            {
                Type = ExceptionModel.EXCEPTION_SCRIPT,
                Title = "Captcha required",
                Message = $"The source requires a captcha to be solved before this {contentKind} can load. Solve the captcha in the window that opened, then retry.",
                CanRetry = true,
                TypeName = nameof(ScriptCaptchaRequiredException)
            }, captchaException);
        }

        private static int NextNumericRepresentationId(XDocument document)
        {
            int max = 0;
            foreach (var representation in document.Descendants().Where(element => element.Name.LocalName == "Representation"))
            {
                if (int.TryParse((string?)representation.Attribute("id"), out var id) && id > max)
                    max = id;
            }
            return max + 1;
        }

        private static string InjectDashSubtitle(string mpd, string subtitleUrl, string lang = "und", string? name = null)
        {
            if (string.IsNullOrWhiteSpace(mpd) || string.IsNullOrWhiteSpace(subtitleUrl))
                return mpd;

            XDocument document;
            try
            {
                // Plugin generated manifests may start with a BOM or whitespace, which the XML declaration does not allow.
                document = XDocument.Parse(mpd.TrimStart('﻿', ' ', '\t', '\r', '\n'), LoadOptions.PreserveWhitespace);
            }
            catch (System.Xml.XmlException e)
            {
                Logger.w<DetailsController>($"Failed to parse the DASH manifest, the selected subtitle is not added: {e.Message}");
                return mpd;
            }

            if (!InjectDashSubtitleIntoDocument(document, subtitleUrl, lang, name))
                return mpd;
            var declaration = document.Declaration != null ? document.Declaration + "\n" : "";
            return declaration + document.ToString(SaveOptions.DisableFormatting);
        }

        /// <summary>
        /// Adds the selected subtitle as the first AdaptationSet of every Period, in the Period's namespace.
        /// dash.js shows the first non-forced text AdaptationSet, so it goes before any embedded ones.
        /// Period segment elements are moved into the existing AdaptationSets first, so the subtitle does not inherit them.
        /// </summary>
        private static bool InjectDashSubtitleIntoDocument(XDocument document, string subtitleUrl, string lang, string? name)
        {
            var periods = document.Root?.Elements().Where(element => element.Name.LocalName == "Period").ToList();
            if (periods == null || periods.Count == 0)
                return false;

            static string Normalize(string value) => value.Replace("&amp;", "&").Trim();
            int representationId = NextNumericRepresentationId(document);
            foreach (var period in periods)
            {
                MoveDashPeriodSegmentElementsToAdaptationSets(period);
                var periodNamespace = period.Name.Namespace;
                var adaptationSet = new XElement(periodNamespace + "AdaptationSet",
                    new XAttribute("mimeType", "text/vtt"),
                    new XAttribute("lang", Normalize(lang)),
                    new XElement(periodNamespace + "Role", new XAttribute("schemeIdUri", "urn:mpeg:dash:role:2011"), new XAttribute("value", "main")),
                    new XElement(periodNamespace + "Label", Normalize(name ?? "Subtitles")),
                    new XElement(periodNamespace + "Representation", new XAttribute("id", representationId), new XAttribute("bandwidth", 256),
                        new XElement(periodNamespace + "BaseURL", Normalize(subtitleUrl))));

                var firstAdaptationSet = period.Elements().FirstOrDefault(element => element.Name.LocalName == "AdaptationSet");
                if (firstAdaptationSet != null)
                {
                    firstAdaptationSet.AddBeforeSelf(adaptationSet);
                }
                else
                {
                    period.Add(adaptationSet);
                }
            }
            return true;
        }

        /// <summary>
        /// Moves Period SegmentBase, SegmentList and SegmentTemplate into each AdaptationSet with the inheritance dash.js applies:
        /// attributes and child groups the AdaptationSet's own element lacks are copied, its own values win.
        /// </summary>
        private static void MoveDashPeriodSegmentElementsToAdaptationSets(XElement period)
        {
            var adaptationSets = period.Elements().Where(element => element.Name.LocalName == "AdaptationSet").ToList();
            foreach (var segmentName in DashSegmentElementNames)
            {
                var periodSegment = period.Elements().FirstOrDefault(element => element.Name.LocalName == segmentName);
                if (periodSegment == null)
                    continue;

                foreach (var adaptationSet in adaptationSets)
                {
                    var ownSegment = adaptationSet.Elements().FirstOrDefault(element => element.Name.LocalName == segmentName);
                    if (ownSegment == null)
                    {
                        var copy = new XElement(periodSegment);
                        var firstRepresentation = adaptationSet.Elements().FirstOrDefault(element => element.Name.LocalName == "Representation");
                        if (firstRepresentation != null)
                        {
                            firstRepresentation.AddBeforeSelf(copy);
                        }
                        else
                        {
                            adaptationSet.Add(copy);
                        }
                        continue;
                    }

                    foreach (var attribute in periodSegment.Attributes().Where(attribute => !attribute.IsNamespaceDeclaration))
                    {
                        if (ownSegment.Attribute(attribute.Name) == null)
                            ownSegment.SetAttributeValue(attribute.Name, attribute.Value);
                    }
                    var ownChildNames = ownSegment.Elements().Select(child => child.Name.LocalName).ToHashSet();
                    foreach (var childGroup in periodSegment.Elements().GroupBy(child => child.Name.LocalName))
                    {
                        if (!ownChildNames.Contains(childGroup.Key))
                            ownSegment.Add(childGroup.Select(child => new XElement(child)));
                    }
                }
                periodSegment.Remove();
            }
        }

        [HttpGet]
        public IActionResult StreamLocalVideoSource(string id) => StreamLocalSource(id);
        [HttpGet]
        public IActionResult StreamLocalAudioSource(string id) => StreamLocalSource(id);
        [HttpGet]
        public IActionResult StreamLocalSubtitleSource(string id) => StreamLocalSource(id);
        [HttpGet]
        public IActionResult LocalDash(string id)
            => Content(this.State().LocalMedia.GetManifest(id), "application/dash+xml");

        private IActionResult StreamLocalSource(string id)
        {
            var (stream, contentType) = this.State().LocalMedia.Open(id);
            return File(stream, contentType, true);
        }
        [HttpGet]
        public IActionResult StreamSubtitleFile(int index)
        {
            var video = EnsureVideo(this.State());
            var source = video.Subtitles[index];
            var uri = source.GetSubtitlesUri()!;
            if (uri.Scheme != "file")
                throw new InvalidOperationException("Must be a file URI.");
            var stream = new FileStream(uri.AbsolutePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            return File(stream, source.Format, enableRangeProcessing: true);
        }


        private static SourceDescriptor DashRawSources(int vindex, int aindex)
        {
            string url = $"/details/SourceDashRaw?videoManifest={vindex}&audioManifest=-1";
            return new SourceDescriptor(url, "application/dash+xml", vindex, -1, -1, false, false, false);
        }

        private static string LocalSourceUrl(WindowState state, string kind, string path, string contentType)
        {
            var id = state.LocalMedia.RegisterFile(path, contentType);
            return $"/Details/StreamLocal{kind}Source?id={id}&windowId={Uri.EscapeDataString(state.WindowID)}";
        }

        private static SourceDescriptor LocalVideoSource(WindowState state, LocalVideoSource sourceVideo)
        {
            var local = EnsureLocal(state);
            int index = local.VideoSources.IndexOf(sourceVideo);
            return new SourceDescriptor(LocalSourceUrl(state, "Video", sourceVideo.FilePath, sourceVideo.Container), sourceVideo.Container, index, -1, -1, true, true, false);
        }
        private static SourceDescriptor LocalAudioSource(WindowState state, LocalAudioSource sourceAudio)
        {
            var local = EnsureLocal(state);
            int index = local.AudioSources.IndexOf(sourceAudio);
            return new SourceDescriptor(LocalSourceUrl(state, "Audio", sourceAudio.FilePath, sourceAudio.Container), sourceAudio.Container, -1, index, -1, true, true, false);
        }
        internal static SourceDescriptor DirectVideoUrlSource(VideoUrlSource sourceVideo, int index, bool isLocal, ProxySettings? proxySettings = null)
        {
            var modifier = (sourceVideo.HasRequestModifier) ? sourceVideo.GetRequestModifier() : null;
            var executor = (sourceVideo.HasRequestExecutor) ? sourceVideo.GetRequestExecutor() : null;

            var videoUrl = proxySettings != null && proxySettings.Value.ShouldProxySources(sourceVideo, null) ? WebUtility.HtmlEncode(HttpProxy.Get(proxySettings.Value.IsLoopback).Add(new HttpProxyRegistryEntry()
            {
                RequestModifier = modifier?.ToProxyFunc(),
                Url = (sourceVideo as VideoUrlSource).Url
            }, proxySettings.Value.ProxyAddress)) : sourceVideo.Url;
            return new SourceDescriptor(videoUrl, sourceVideo.Container)
            {
                VideoIndex = index,
                VideoIsLocal = isLocal
            };
        }
        internal static SourceDescriptor DirectAudioUrlSource(AudioUrlSource sourceAudio, int index, bool isLocal, ProxySettings? proxySettings = null)
        {
            var modifier = (sourceAudio.HasRequestModifier) ? sourceAudio.GetRequestModifier() : null;
            var executor = (sourceAudio.HasRequestExecutor) ? sourceAudio.GetRequestExecutor() : null;

            var audioUrl = proxySettings != null && proxySettings.Value.ShouldProxySources(null, sourceAudio) ? WebUtility.HtmlEncode(HttpProxy.Get(proxySettings.Value.IsLoopback).Add(new HttpProxyRegistryEntry()
            {
                RequestModifier = modifier?.ToProxyFunc(),
                Url = (sourceAudio as AudioUrlSource).Url
            }, proxySettings.Value.ProxyAddress)) : sourceAudio.Url;
            return new SourceDescriptor(audioUrl, sourceAudio.Container)
            {
                AudioIndex = index,
                AudioIsLocal = isLocal
            };
        }

        private static SourceDescriptor DirectHLSUrlSource(WindowState state, int videoIndex = -1, int audioIndex = -1, int subtitleIndex = -1, bool subtitleIsLocal = false, ProxySettings? proxySettings = null, string? modifierId = null)
        {
            (var sourceVideo, var sourceAudio, _) = GetSources(state, videoIndex, audioIndex, -1, false, false, false);
            if (subtitleIndex >= 0 || (videoIndex >= 0 && audioIndex >= 0))
            {
                return new SourceDescriptor(
                    $"/details/SourceHLS?videoIndex={videoIndex}&audioIndex={audioIndex}"
                    + $"&subtitleIndex={subtitleIndex}&subtitleIsLocal={subtitleIsLocal}"
                    + $"&isLoopback={proxySettings?.IsLoopback ?? true}"
                    + $"&windowId={state.WindowID}"
                    + (!string.IsNullOrEmpty(modifierId) ? $"&modifierId={Uri.EscapeDataString(modifierId)}" : ""),
                    "application/vnd.apple.mpegurl",
                    videoIndex, audioIndex, subtitleIndex,
                    false, false, subtitleIsLocal
                );
            }

            if (sourceVideo is HLSVariantVideoUrlSource)
                return new SourceDescriptor($"/details/SourceHLS?videoIndex={videoIndex}&audioIndex={audioIndex}&subtitleIndex={subtitleIndex}&subtitleIsLocal={subtitleIsLocal}&windowId={state.WindowID}", "application/vnd.apple.mpegurl");
            if (sourceAudio is HLSVariantAudioUrlSource)
                return new SourceDescriptor($"/details/SourceHLS?videoIndex=-1&audioIndex={audioIndex}&subtitleIndex={subtitleIndex}&subtitleIsLocal={subtitleIsLocal}&windowId={state.WindowID}", "application/vnd.apple.mpegurl");
            if (sourceVideo is HLSManifestSource hlsm)
            {
                if (proxySettings != null && proxySettings.Value.ShouldProxySources(sourceVideo, sourceAudio))
                {
                    return new SourceDescriptor(
                        $"/details/SourceHLS?videoIndex={videoIndex}&isLoopback={proxySettings?.IsLoopback ?? true}&windowId={state.WindowID}",
                        "application/vnd.apple.mpegurl",
                        videoIndex, -1, -1, false, false, false
                    );
                }
                return new SourceDescriptor(hlsm.Url, "application/vnd.apple.mpegurl", videoIndex, -1, -1, false, false, false);
            }

            if (sourceAudio is HLSManifestAudioSource hlsa)
            {
                if (proxySettings != null && proxySettings.Value.ShouldProxySources(sourceVideo, sourceAudio))
                {
                    return new SourceDescriptor(
                        $"/details/SourceHLS?audioIndex={audioIndex}&videoIndex=-1&isLoopback={proxySettings?.IsLoopback ?? true}&windowId={state.WindowID}",
                        "application/vnd.apple.mpegurl",
                        -1, audioIndex, -1, false, false, false
                    );
                }
                return new SourceDescriptor(hlsa.Url, "application/vnd.apple.mpegurl", -1, audioIndex, -1, false, false, false);
            }

            throw new Exception("Expected either HLS audio or video source.");
        }

        [HttpGet]
        public bool WatchProgress(string url, long position)
        {
            var state = this.State().DetailsState;
            if (url == null)
                return false;
            if (url == state.VideoLoaded?.Url && state.VideoHistoryIndex.Url == url)
            {
                state.LiveChatManager?.SetVideoPosition(position);
                try
                {
                    if (state.VideoPlaybackTracker?.ShouldUpdate() ?? false)
                        state.VideoPlaybackTracker?.OnProgress((double)position / 1000, true);
                }
                catch(Exception ex)
                {
                    Logger.w(nameof(DetailsController), $"Failed to call onProgress on PlaybackTracker: " + ex.Message, ex);
                }

                var video = state.VideoLoaded;
                var history = state.VideoHistoryIndex;
                if(state._lastWatchPositionChange == DateTime.MinValue)
                {
                    state._lastWatchPosition = position;
                    state._lastWatchPositionChange = DateTime.Now;
                    return false;
                }
                if (DateTime.Now.Subtract(state._lastWatchPositionChange) > TimeSpan.FromSeconds(1))
                {
                    long delta = position - state._lastWatchPosition;
                    if (delta < 0)
                        return false;
                    state._lastWatchPosition = position;
                    state._lastWatchPositionChange = DateTime.Now;

                    Logger.v(nameof(DetailsController), $"Progress {url} - {position} - {delta} (PlaybackTracker: " + (state.VideoPlaybackTracker != null).ToString() + ")");

                    StateHistory.UpdateHistory(video, history, position / 1000, delta);
                    if (state.VideoSubscription != null)// && GrayjaySettings.Instance.Subscriptions.AllowPlaytimeTracking)
                        state.VideoSubscription.UpdateWatchTime((int)(delta / 1000));
                    return true;
                }
            }
            return false;
        }




        /// <summary>
        /// Thrown when the video changed while a DASH manifest was generated for the previous one.
        /// </summary>
        internal sealed class SupersededDashRequestException : Exception
        {
        }

        public class VideoLoadResult
        {
            public PlatformVideoDetails Video { get; set; }
            public VideoLocal Local { get; set; }
        }

        public class PostLoadResult
        {
            public PlatformPostDetails Post { get; set; }
        }

        public class SourceDescriptor
        {
            public string Url { get; set; }
            public string Type { get; set; }

            public int VideoIndex { get; set; }
            public bool VideoIsLocal { get; set; }
            public int AudioIndex { get; set; }
            public bool AudioIsLocal { get; set; }
            public int SubtitleIndex { get; set; }
            public bool SubtitleIsLocal { get; set; }

            public SourceDescriptor() { }
            public SourceDescriptor(string url, string type, int videoIndex = -1, int audioIndex = -1, int subtitleIndex = -1, bool videoIsLocal = false, bool audioIsLocal = false, bool subtitleIsLocal = false)
            {
                Url = url; 
                Type = type; 
                VideoIndex = videoIndex;
                AudioIndex = audioIndex;
                SubtitleIndex = subtitleIndex;
                VideoIsLocal = videoIsLocal;
                AudioIsLocal = audioIsLocal;
                SubtitleIsLocal = subtitleIsLocal;
            }
        }

        private static string GetBaseUri(WindowState state, ProxySettings? proxySettings)
        {
            if (proxySettings != null && proxySettings.Value.ExposeLocalAsAny && proxySettings.Value.ProxyAddress != null)
                return $"http://{proxySettings.Value.ProxyAddress.ToUrlAddress()}:{GrayjayCastingServer.Instance.BaseUri!.Port}";

            return GrayjayServer.Instance.BaseUrl;
        }

        internal static string BuildSubtitleUrl(WindowState state, int subtitleIndex, bool subtitleIsLocal, ProxySettings? proxySettings, string? modifierId = null)
        {
            var baseUri = GetBaseUri(state, proxySettings);
            var url = $"{baseUri}/Details/Subtitle?subtitleIndex={subtitleIndex}"
                + $"&subtitleIsLocal={subtitleIsLocal}"
                + $"&windowId={state.WindowID}";

            if (subtitleIsLocal)
            {
                url += $"&localMediaId={RegisterLocalSubtitle(state, subtitleIndex)}";
            }

            if (!string.IsNullOrEmpty(modifierId))
                url += $"&modifierId={Uri.EscapeDataString(modifierId)}";

            return url;
        }

        public static string RegisterLocalSubtitle(WindowState state, int index)
        {
            var source = EnsureLocal(state).SubtitleSources[index];
            return state.LocalMedia.RegisterFile(source.FilePath, string.IsNullOrWhiteSpace(source.Format) ? "text/vtt" : source.Format);
        }

        public static async Task<(byte[] Bytes, string ContentType)> GetSubtitleBytesAsync(WindowState state, int subtitleIndex, bool subtitleIsLocal, string? modifierId = null, string? localMediaId = null, bool asVtt = false)
        {
            var (bytes, contentType) = await GetRawSubtitleBytesAsync(state, subtitleIndex, subtitleIsLocal, modifierId, localMediaId);
            if (asVtt) return (VttHelper.ToWebVtt(contentType, bytes), "text/vtt");
            return (VttHelper.IsVtt(contentType, bytes) ? VttHelper.StripUnsupportedTags(bytes) : bytes, contentType);
        }

        private static async Task<(byte[] Bytes, string ContentType)> GetRawSubtitleBytesAsync(WindowState state, int subtitleIndex, bool subtitleIsLocal, string? modifierId, string? localMediaId)
        {
            if (subtitleIsLocal)
            {
                if (localMediaId == null)
                    throw new BadHttpRequestException("Local subtitle identity is required", StatusCodes.Status404NotFound);
                var (stream, localContentType) = state.LocalMedia.Open(localMediaId);
                using (stream)
                using (var output = new MemoryStream())
                {
                    await stream.CopyToAsync(output);
                    return (output.ToArray(), localContentType);
                }
            }

            var video = EnsureVideo(state);
            var srcRemote = video.Subtitles[subtitleIndex];
            var contentType = string.IsNullOrWhiteSpace(srcRemote.Format) ? "text/vtt" : srcRemote.Format!;

            var uri = srcRemote.GetSubtitlesUri();
            if (uri == null)
                uri = new Uri(srcRemote.Url);

            if (uri.Scheme.Equals("file", StringComparison.OrdinalIgnoreCase))
            {
                var bytes = await System.IO.File.ReadAllBytesAsync(uri.LocalPath);
                return (bytes, contentType);
            }

            IRequestModifier? modifier = null;
            if (!string.IsNullOrEmpty(modifierId))
                DetailsState.Modifiers.TryGetValue(modifierId, out modifier);

            if (modifier != null)
            {
                var headers = new Grayjay.Engine.Models.HttpHeaders();
                var res = ModifierHttp.GetBytes(new ManagedHttpClient(), uri.ToString(), modifier, headers, decodeContent: true);
                if (!res.IsOk)
                    throw new InvalidDataException($"Failed to fetch subtitle [{res.Code}]");

                return (res.Bytes, contentType);
            }

            using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true });
            var b = await client.GetByteArrayAsync(uri);
            return (b, contentType);
        }

        private static double? TryGetVideoDurationSeconds(WindowState state)
        {
            var v = state.DetailsState.VideoLoaded;
            if (v == null)
                return null;

            object? videoObj = v;
            foreach (var name in new[] { "DurationSeconds", "Duration" })
            {
                var p = videoObj.GetType().GetProperty(name);
                if (p != null)
                {
                    var val = p.GetValue(videoObj);
                    if (val is int i) return i;
                    if (val is long l) return l;
                    if (val is double d) return d;
                    if (val is float f) return f;
                }
            }

            var innerVideo = v.GetType().GetProperty("Video")?.GetValue(v);
            if (innerVideo != null)
            {
                foreach (var name in new[] { "DurationSeconds", "Duration" })
                {
                    var p = innerVideo.GetType().GetProperty(name);
                    if (p != null)
                    {
                        var val = p.GetValue(innerVideo);
                        if (val is int i) return i;
                        if (val is long l) return l;
                        if (val is double d) return d;
                        if (val is float f) return f;
                    }
                }
            }

            return null;
        }

        private static string GenerateSubtitleMediaPlaylist(string subtitleUrl, double durationSeconds)
        {
            if (durationSeconds <= 0)
                durationSeconds = 60;

            var targetDuration = Math.Max(1, (int)Math.Ceiling(durationSeconds));
            var dur = durationSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return
                "#EXTM3U\n" +
                "#EXT-X-VERSION:3\n" +
                $"#EXT-X-TARGETDURATION:{targetDuration}\n" +
                "#EXT-X-MEDIA-SEQUENCE:0\n" +
                $"#EXTINF:{dur},\n" +
                subtitleUrl + "\n" +
                "#EXT-X-ENDLIST\n";
        }

        [HttpGet]
        public async Task<IActionResult> Subtitle(int subtitleIndex, bool subtitleIsLocal = false, string? modifierId = null, string? localMediaId = null, bool asVtt = false)
        {
            Response.Headers["Access-Control-Allow-Origin"] = "*";

            var state = this.State();
            var (bytes, ct) = await GetSubtitleBytesAsync(state, subtitleIndex, subtitleIsLocal, modifierId, localMediaId, asVtt);
            return File(bytes, ct);
        }

        [HttpGet]
        public IActionResult SubtitleHLS(int subtitleIndex, bool subtitleIsLocal = false, string? modifierId = null)
        {
            Response.Headers["Access-Control-Allow-Origin"] = "*";

            var state = this.State();

            var baseUri = $"{Request.Scheme}://{Request.Host.Value}";
            var subtitleUrl =
                $"{baseUri}/Details/Subtitle?subtitleIndex={subtitleIndex}"
                + $"&subtitleIsLocal={subtitleIsLocal}"
                + $"&windowId={state.WindowID}"
                + (!string.IsNullOrEmpty(modifierId) ? $"&modifierId={Uri.EscapeDataString(modifierId)}" : "");

            if (subtitleIsLocal) subtitleUrl += $"&localMediaId={RegisterLocalSubtitle(state, subtitleIndex)}";
            subtitleUrl += "&asVtt=true";
            var dur = TryGetVideoDurationSeconds(state) ?? 86400.0;
            var m3u8 = GenerateSubtitleMediaPlaylist(subtitleUrl, dur);

            return Content(m3u8, "application/x-mpegurl");
        }
    }
}
