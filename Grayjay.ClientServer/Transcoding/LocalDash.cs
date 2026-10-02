using System.Collections.Concurrent;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Grayjay.Engine.Models.Video.Sources;

namespace Grayjay.ClientServer.Transcoding;

public static class LocalDash
{
    private sealed class CacheState
    {
        public readonly ConcurrentDictionary<string, Lazy<Task<string>>> Remuxes = new();
        public readonly ConcurrentDictionary<ManifestKey, Lazy<Task<string>>> Manifests = new();
    }
    private sealed record ManifestKey(string Source, string BaseUrl, string WindowId, string? SubtitleUrl, string? SubtitleType, string? SubtitleName, string? SubtitleLanguage);
    private static readonly ConditionalWeakTable<LocalMediaRegistry, CacheState> Cache = new();
    private static readonly ConcurrentDictionary<string, object> PreparationLocks = new();

    public static Task<string> GenerateAsync(LocalMediaRegistry registry, LocalVideoSource video, LocalAudioSource? audio, string baseUrl, string windowId, string? subtitleUrl, string? subtitleType, string? subtitleName, string? subtitleLanguage = null)
    {
        var key = registry.RegisterFile(video.FilePath, video.Container);
        if (audio != null) key += registry.RegisterFile(audio.FilePath, audio.Container);
        return GenerateCachedAsync(registry, key, () => Task.Run(() => BindPersistentMedia(registry, PrepareDownload(video, audio))), baseUrl, windowId, subtitleUrl, subtitleType, subtitleName, subtitleLanguage);
    }

    public static Task<string> GenerateRemoteAsync(LocalMediaRegistry registry, VideoUrlSource video, Func<string> inputUrl, string baseUrl, string windowId, string? subtitleUrl, string? subtitleType, string? subtitleName, string? subtitleLanguage = null)
        => GenerateCachedAsync(registry, "remote\n" + video.Url, () =>
        {
            var input = inputUrl();
            return Task.Run(() => BindPersistentMedia(registry, Remux(registry.CreateTemporaryDirectory(), input, null)));
        }, baseUrl, windowId, subtitleUrl, subtitleType, subtitleName, subtitleLanguage);

    public static Task<string> GenerateUnindexedAsync(LocalMediaRegistry registry, string sourceKey, Func<(string Video, string? Audio)> inputs,
        string baseUrl, string windowId, string? subtitleUrl, string? subtitleType, string? subtitleName, string? subtitleLanguage = null)
        => GenerateCachedAsync(registry, sourceKey, () =>
        {
            var input = inputs();
            return Task.Run(() => BindPersistentMedia(registry, Remux(registry.CreateTemporaryDirectory(), input.Video, input.Audio)));
        }, baseUrl, windowId, subtitleUrl, subtitleType, subtitleName, subtitleLanguage);

    private static async Task<string> GenerateCachedAsync(LocalMediaRegistry registry, string sourceKey, Func<Task<string>> prepare, string baseUrl, string windowId, string? subtitleUrl, string? subtitleType, string? subtitleName, string? subtitleLanguage = null)
    {
        var cache = Cache.GetValue(registry, _ => new CacheState());
        var key = new ManifestKey(sourceKey, baseUrl, windowId, subtitleUrl, subtitleType, subtitleName, subtitleLanguage);
        var task = cache.Manifests.GetOrAdd(key, _ => new Lazy<Task<string>>(async () =>
        {
            var remux = cache.Remuxes.GetOrAdd(sourceKey, _ => new Lazy<Task<string>>(prepare));
            string mediaManifest;
            try { mediaManifest = await remux.Value; }
            catch
            {
                ((ICollection<KeyValuePair<string, Lazy<Task<string>>>>)cache.Remuxes).Remove(new(sourceKey, remux));
                throw;
            }
            return BuildManifest(mediaManifest, baseUrl, windowId, subtitleUrl, subtitleType, subtitleName, subtitleLanguage);
        }));
        try { return await task.Value; }
        catch
        {
            ((ICollection<KeyValuePair<ManifestKey, Lazy<Task<string>>>>)cache.Manifests).Remove(new(key, task));
            throw;
        }
    }

    private static string BuildManifest(string manifest, string baseUrl, string windowId, string? subtitleUrl, string? subtitleType, string? subtitleName, string? subtitleLanguage = null)
    {
        var document = XDocument.Parse(manifest);
        XNamespace ns = "urn:mpeg:dash:schema:mpd:2011";
        foreach (var url in document.Descendants(ns + "BaseURL"))
            url.Value = $"{baseUrl}/Details/StreamLocalVideoSource?id={url.Value}&windowId={Uri.EscapeDataString(windowId)}";
        if (subtitleUrl != null)
        {
            document.Descendants(ns + "Period").First().Add(new XElement(ns + "AdaptationSet",
                new XAttribute("contentType", "text"), new XAttribute("lang", Grayjay.Engine.Models.Subtitles.SubtitleLanguage.Resolve(subtitleLanguage, subtitleName)),
                new XElement(ns + "Role", new XAttribute("schemeIdUri", "urn:mpeg:dash:role:2011"), new XAttribute("value", "subtitle")),
                new XElement(ns + "Role", new XAttribute("schemeIdUri", "urn:mpeg:dash:role:2011"), new XAttribute("value", "main")),
                new XElement(ns + "Label", subtitleName ?? "Subtitles"),
                new XElement(ns + "Representation", new XAttribute("id", "subtitles"),
                    new XAttribute("mimeType", subtitleType ?? "text/vtt"), new XAttribute("bandwidth", "1000"),
                    new XElement(ns + "BaseURL", subtitleUrl))));
        }
        return document.ToString();
    }

    public static string PrepareDownload(LocalVideoSource video, LocalAudioSource? audio)
    {
        static string Identity(string path)
        {
            var file = new FileInfo(path);
            return $"{file.FullName}\n{file.Length}\n{file.LastWriteTimeUtc.Ticks}";
        }
        string Fingerprint() => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("dash-v1\n" + Identity(video.FilePath) + "\n" + (audio != null ? Identity(audio.FilePath) : "muxed"))));
        var fingerprint = Fingerprint();
        var directory = Path.Combine(video.FilePath + ".dash", fingerprint);
        lock (PreparationLocks.GetOrAdd(directory, _ => new object()))
        {
            var manifest = Path.Combine(directory, "manifest.mpd");
            if (IsPrepared(manifest)) return manifest;
            Directory.CreateDirectory(Path.GetDirectoryName(directory)!);
            var staging = directory + ".building-" + Guid.NewGuid();
            Directory.CreateDirectory(staging);
            try
            {
                Remux(staging, video.FilePath, audio?.FilePath);
                File.WriteAllText(Path.Combine(staging, "artifacts.json"), JsonSerializer.Serialize(
                    Directory.GetFiles(staging, "*.mp4").ToDictionary(Path.GetFileName, path => new FileInfo(path).Length)));
                if (Fingerprint() != fingerprint) throw new IOException("Downloaded media changed while preparing DASH.");
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
                Directory.Move(staging, directory);
                return manifest;
            }
            finally
            {
                if (Directory.Exists(staging)) Directory.Delete(staging, true);
            }
        }
    }

    public static void DeleteDownloadCache(string videoPath)
    {
        var directory = videoPath + ".dash";
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }

    private static bool IsPrepared(string manifest)
    {
        if (!File.Exists(manifest)) return false;
        try
        {
            var document = XDocument.Load(manifest);
            var urls = document.Descendants().Where(x => x.Name.LocalName == "BaseURL").ToArray();
            var lengths = JsonSerializer.Deserialize<Dictionary<string, long>>(File.ReadAllText(Path.Combine(Path.GetDirectoryName(manifest)!, "artifacts.json")));
            return urls.Length > 0 && urls.All(x => Path.GetFileName(x.Value) == x.Value
                && lengths != null && lengths.TryGetValue(x.Value, out var length) && length > 0
                && File.Exists(Path.Combine(Path.GetDirectoryName(manifest)!, x.Value))
                && new FileInfo(Path.Combine(Path.GetDirectoryName(manifest)!, x.Value)).Length == length);
        }
        catch (System.Xml.XmlException) { return false; }
        catch (JsonException) { return false; }
        catch (IOException) { return false; }
    }

    private static string BindPersistentMedia(LocalMediaRegistry registry, string path)
    {
        var document = XDocument.Load(path);
        XNamespace ns = "urn:mpeg:dash:schema:mpd:2011";
        foreach (var representation in document.Descendants(ns + "Representation"))
        {
            var url = representation.Element(ns + "BaseURL")!;
            url.Value = registry.RegisterFile(Path.Combine(Path.GetDirectoryName(path)!, url.Value), representation.Attribute("mimeType")!.Value);
        }
        return document.ToString();
    }

    private static string Remux(string directory, string input, string? audio)
    {
        var path = Path.Combine(directory, "manifest.mpd");
        var args = new List<string> { "-hide_banner", "-nostdin", "-y", "-i", input };
        if (audio != null) args.AddRange(new[] { "-i", audio });
        args.AddRange(new[] { "-map", "0:v:0" });
        args.AddRange(audio != null ? new[] { "-map", "1:a:0" } : new[] { "-map", "0:a?" });
        args.AddRange(new[] { "-c", "copy", "-f", "dash", "-dash_segment_type", "mp4", "-single_file", "1", "-global_sidx", "1", "-use_template", "0", path });
        if (FFMPEG.ExecuteSafe(args.ToArray()) != 0)
            throw new InvalidOperationException("Could not prepare the downloaded video for DASH playback.");
        var document = XDocument.Load(path);
        XNamespace ns = "urn:mpeg:dash:schema:mpd:2011";
        document.Root!.SetAttributeValue("profiles", "urn:mpeg:dash:profile:isoff-on-demand:2011");
        foreach (var representation in document.Descendants(ns + "Representation"))
        {
            var url = representation.Element(ns + "BaseURL")!;
            var mediaPath = Path.Combine(directory, url.Value);
            var (start, end) = FindIndex(mediaPath);
            representation.Element(ns + "SegmentList")!.ReplaceWith(new XElement(ns + "SegmentBase",
                new XAttribute("indexRange", $"{start}-{end}"), new XAttribute("indexRangeExact", "true"),
                new XElement(ns + "Initialization", new XAttribute("range", $"0-{start - 1}"))));
        }
        document.Save(path);
        return path;
    }

    private static (long Start, long End) FindIndex(string path)
    {
        using var stream = File.OpenRead(path);
        Span<byte> header = stackalloc byte[8];
        while (stream.Position < stream.Length)
        {
            var start = stream.Position;
            stream.ReadExactly(header);
            long size = BinaryPrimitives.ReadUInt32BigEndian(header);
            var isIndex = header[4..].SequenceEqual("sidx"u8);
            if (size == 1)
            {
                stream.ReadExactly(header);
                size = checked((long)BinaryPrimitives.ReadUInt64BigEndian(header));
            }
            if (size < 8 || size > stream.Length - start) break;
            if (isIndex) return (start, start + size - 1);
            stream.Position = start + size;
        }
        throw new InvalidDataException("Remuxed download has no DASH segment index.");
    }
}
