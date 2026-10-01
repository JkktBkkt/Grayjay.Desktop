using System.Collections.Concurrent;
using System.Diagnostics;
using Grayjay.ClientServer.Sabr.Proto;
using Grayjay.Desktop.POC;
using Grayjay.Engine.Models.Video.Sources;

namespace Grayjay.ClientServer.Sabr
{
    public static class UmpDownloader
    {
        private const string TAG = "UmpDownloader";
        private const int PARALLEL_MIN_SLICE_SEC = 30;
        private const int MAX_CONCURRENCY = 6;
        private const long DOWNLOAD_KEEP_BEHIND_US = 0;
        private static readonly TimeSpan POLL = TimeSpan.FromSeconds(2);
        private static readonly TimeSpan MAX_STALL = TimeSpan.FromSeconds(90);

        public class Result
        {
            public long Length { get; init; }
            public FormatInitializationMetadata? FormatInitialization { get; init; }

            public StreamMetaData? ToStreamMetaData()
            {
                var init = FormatInitialization;
                if (init?.InitRange == null || init.IndexRange == null || init.IndexRange.End <= 0)
                    return null;
                return new StreamMetaData()
                {
                    FileInitStart = (int)init.InitRange.Start,
                    FileInitEnd = (int)init.InitRange.End,
                    FileIndexStart = (int)init.IndexRange.Start,
                    FileIndexEnd = (int)init.IndexRange.End
                };
            }
        }

        private sealed class Track(int role, UMPFormat format, string targetFile)
        {
            public int Role { get; } = role;
            public UMPFormat Format { get; } = format;
            public string TargetFile { get; } = targetFile;
            public string TempDirectory => TargetFile + ".umpparts";
            public ConcurrentDictionary<int, bool> Claimed { get; } = new();
            public long InitSize;
            public long SegmentBytes;
            public int EndSegment;
            public byte[]? InitBytes;
            public FormatInitializationMetadata? FormatInitialization;
        }

        public static async Task<Result> DownloadTrackAsync(SabrStreamSpec spec, int role, UMPFormat format, long durationSec, string targetFile, int concurrency,
            Action<long, long, long> onProgress, CancellationToken cancellationToken = default)
        {
            var result = await DownloadAsync(spec,
                role == SabrSession.ROLE_VIDEO ? format : null, role == SabrSession.ROLE_VIDEO ? targetFile : null,
                role == SabrSession.ROLE_AUDIO ? format : null, role == SabrSession.ROLE_AUDIO ? targetFile : null,
                durationSec, concurrency, (_, estimate, read, speed) => onProgress(estimate, read, speed), cancellationToken);
            return (role == SabrSession.ROLE_VIDEO ? result.Video : result.Audio)!;
        }

        public static async Task<(Result? Video, Result? Audio)> DownloadAsync(SabrStreamSpec spec,
            UMPFormat? video, string? videoFile, UMPFormat? audio, string? audioFile, long durationSec, int concurrency,
            Action<int, long, long, long> onProgress, CancellationToken cancellationToken = default)
        {
            if (spec.IsLive) throw new InvalidOperationException("Live streams cannot be downloaded");
            var tracks = new List<Track>();
            if (video != null && videoFile != null) tracks.Add(new Track(SabrSession.ROLE_VIDEO, video, videoFile));
            if (audio != null && audioFile != null) tracks.Add(new Track(SabrSession.ROLE_AUDIO, audio, audioFile));
            if (tracks.Count == 0) throw new ArgumentException("No UMP tracks selected");

            var maxBySlice = durationSec > 0 ? (int)(durationSec / PARALLEL_MIN_SLICE_SEC) : 1;
            var n = Math.Min(Math.Clamp(concurrency, 1, MAX_CONCURRENCY), Math.Max(maxBySlice, 1));
            var totalUs = durationSec * 1_000_000L;
            if (totalUs <= 0) n = 1;
            var stopwatch = Stopwatch.StartNew();
            var progressLock = new object();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            void Report(Track track)
            {
                lock (progressLock)
                {
                    var init = Interlocked.Read(ref track.InitSize);
                    var segments = Interlocked.Read(ref track.SegmentBytes);
                    var count = track.Claimed.Count;
                    var end = Volatile.Read(ref track.EndSegment);
                    var written = init + segments;
                    var fallback = Math.Max(0, (long)track.Format.Bitrate / 8 * durationSec);
                    var estimate = end > 0 && count > 0 ? init + segments * end / count : fallback;
                    var speed = stopwatch.ElapsedMilliseconds > 0 ? written * 1000 / stopwatch.ElapsedMilliseconds : 0;
                    onProgress(track.Role, Math.Max(estimate, written), written, speed);
                }
            }

            async Task WaitAll(List<Task> tasks)
            {
                try { await Task.WhenAll(tasks); }
                catch
                {
                    var errors = tasks.Where(x => x.IsFaulted).SelectMany(x => x.Exception!.Flatten().InnerExceptions);
                    var error = errors.FirstOrDefault(x => x is not OperationCanceledException) ?? errors.FirstOrDefault();
                    if (error != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
                    throw;
                }
            }

            async Task RunSlice(int i)
            {
                var startUs = totalUs > 0 ? i * totalUs / n : 0;
                var endUs = i == n - 1 ? long.MaxValue : (i + 1) * totalUs / n;
                using var session = spec.CreateSession();
                session.KeepBehindUs = DOWNLOAD_KEEP_BEHIND_US;
                var positions = tracks.ToDictionary(x => x.Role, _ => startUs);
                var positionLock = new object();
                session.SetPlaybackPosition(startUs);
                foreach (var track in tracks) session.SetDemand(track.Role, track.Format, startUs);
                if (startUs > 0) session.Restart(startUs, true);
                session.Start();

                async Task RunTrack(Track track)
                {
                    var format = track.Format;
                    var buffer = session.BufferFor(format);
                    try
                    {
                        if (i == 0)
                        {
                            var deadline = DateTime.UtcNow + MAX_STALL;
                            SabrSegment? init = null;
                            while (init == null)
                            {
                                CheckState(session, linked.Token);
                                init = await buffer.AwaitInitAsync(POLL, linked.Token);
                                if (init == null && DateTime.UtcNow > deadline)
                                    throw new SabrException($"UMP init segment for itag {format.Itag} never arrived");
                            }
                            track.InitBytes = init.ToByteArray();
                            Interlocked.Exchange(ref track.InitSize, track.InitBytes.Length);
                            Report(track);
                        }

                        using var output = new FileStream(Path.Combine(track.TempDirectory, $"part_{i}"), FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, true);
                        var nextSeq = -1;
                        var lastProgress = DateTime.UtcNow;
                        while (true)
                        {
                            CheckState(session, linked.Token);
                            var segment = nextSeq < 0
                                ? await buffer.AwaitCoveringAsync(startUs, POLL, linked.Token)
                                : await buffer.AwaitSequenceAsync(nextSeq, POLL, linked.Token);
                            if (segment != null && !segment.IsComplete && !await buffer.AwaitCompleteAsync(segment, POLL, linked.Token))
                                segment = null;

                            var fi = session.FormatInitializationFor(format);
                            if (fi != null)
                            {
                                Interlocked.CompareExchange(ref track.FormatInitialization, fi, null);
                                if (fi.EndSegmentNumber > 0) Volatile.Write(ref track.EndSegment, fi.EndSegmentNumber);
                            }
                            if (segment == null)
                            {
                                if (session.IsComplete(format)) break;
                                if (DateTime.UtcNow - lastProgress > MAX_STALL)
                                    throw new SabrException($"UMP download stalled for itag {format.Itag} before reaching the end");
                                continue;
                            }
                            if (segment.StartUs >= endUs) break;
                            nextSeq = segment.SequenceNumber + 1;
                            if (track.Claimed.TryAdd(segment.SequenceNumber, true))
                            {
                                var bytes = segment.ToByteArray();
                                await output.WriteAsync(bytes, linked.Token);
                                Interlocked.Add(ref track.SegmentBytes, bytes.Length);
                                Report(track);
                            }
                            lock (positionLock)
                            {
                                positions[track.Role] = segment.EndUs;
                                session.SetDemand(track.Role, format, segment.EndUs);
                                session.SetPlaybackPosition(positions.Values.Min());
                            }
                            lastProgress = DateTime.UtcNow;
                            buffer.EvictBeforeSequence(segment.SequenceNumber);
                            var lastSeg = Volatile.Read(ref track.EndSegment);
                            if (lastSeg > 0 && nextSeq > lastSeg) break;
                        }
                    }
                    catch
                    {
                        linked.Cancel();
                        throw;
                    }
                    finally
                    {
                        lock (positionLock)
                        {
                            positions.Remove(track.Role);
                            session.ClearDemand(track.Role);
                            if (positions.Count > 0) session.SetPlaybackPosition(positions.Values.Min());
                        }
                    }
                }
                await WaitAll(tracks.Select(RunTrack).ToList());
            }

            try
            {
                foreach (var track in tracks)
                {
                    if (File.Exists(track.TargetFile)) File.Delete(track.TargetFile);
                    if (Directory.Exists(track.TempDirectory)) Directory.Delete(track.TempDirectory, true);
                    Directory.CreateDirectory(track.TempDirectory);
                }
                var tasks = Enumerable.Range(0, n).Select(i => Task.Run(async () =>
                {
                    try { await RunSlice(i); }
                    catch { linked.Cancel(); throw; }
                })).ToList();
                await WaitAll(tasks);

                var results = new Dictionary<int, Result>();
                foreach (var track in tracks)
                {
                    var expected = Volatile.Read(ref track.EndSegment);
                    if (expected > 0)
                    {
                        var missing = Enumerable.Range(1, expected).Where(x => !track.Claimed.ContainsKey(x)).ToList();
                        if (missing.Count > 0)
                            throw new SabrException($"UMP download for itag {track.Format.Itag} is missing {missing.Count} segment(s), first {missing[0]}");
                    }
                    long total = 0;
                    using (var output = new FileStream(track.TargetFile, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, true))
                    {
                        if (track.InitBytes != null)
                        {
                            await output.WriteAsync(track.InitBytes, cancellationToken);
                            total += track.InitBytes.Length;
                        }
                        for (var i = 0; i < n; i++)
                        {
                            using var input = File.OpenRead(Path.Combine(track.TempDirectory, $"part_{i}"));
                            await input.CopyToAsync(output, cancellationToken);
                            total += input.Length;
                        }
                    }
                    onProgress(track.Role, total, total, 0);
                    Logger.i(TAG, $"Downloaded itag {track.Format.Itag} ({total} bytes, {track.Claimed.Count} segments, {n} shared slices) in {stopwatch.Elapsed.TotalSeconds:F1}s");
                    results[track.Role] = new Result { Length = total, FormatInitialization = track.FormatInitialization };
                }
                return (results.GetValueOrDefault(SabrSession.ROLE_VIDEO), results.GetValueOrDefault(SabrSession.ROLE_AUDIO));
            }
            finally
            {
                foreach (var track in tracks)
                    try { if (Directory.Exists(track.TempDirectory)) Directory.Delete(track.TempDirectory, true); } catch { }
            }
        }

        private static void CheckState(SabrSession session, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fatal = session.FatalError;
            if (fatal != null) throw fatal;
        }
    }
}
