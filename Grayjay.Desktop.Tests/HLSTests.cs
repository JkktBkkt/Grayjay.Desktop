using Grayjay.ClientServer.Parsers;

namespace Grayjay.Desktop.Tests;

[TestClass]
public class HLSTests
{
    private const string SourceUrl = "https://playback.media-streaming.soundcloud.cloud/media/1234/playlist.m3u8";

    private const string MediaPlaylist = """
    #EXTM3U
    #EXT-X-VERSION:7
    #EXT-X-TARGETDURATION:10
    #EXT-X-MEDIA-SEQUENCE:0
    #EXT-X-PLAYLIST-TYPE:VOD
    #EXT-X-MAP:URI="https://cf-media.sndcdn.com/init.mp4"
    #EXTINF:9.986,
    https://cf-media.sndcdn.com/segment0.m4s
    #EXTINF:9.986,
    https://cf-media.sndcdn.com/segment1.m4s
    #EXT-X-ENDLIST
    """;

    private const string MasterPlaylist = """
    #EXTM3U
    #EXT-X-VERSION:4
    #EXT-X-INDEPENDENT-SEGMENTS
    #EXT-X-STREAM-INF:BANDWIDTH=1280000,RESOLUTION=1280x720,CODECS="avc1.4d401f,mp4a.40.2"
    https://example.com/720p.m3u8
    #EXT-X-STREAM-INF:BANDWIDTH=640000,RESOLUTION=854x480,CODECS="avc1.4d401e,mp4a.40.2"
    https://example.com/480p.m3u8
    """;

    [TestMethod]
    public void TestParseMasterPlaylistRejectsMediaPlaylist()
    {
        Assert.ThrowsException<InvalidDataException>(() => HLS.ParseMasterPlaylist(MediaPlaylist, SourceUrl));
    }

    [TestMethod]
    public void TestParseMasterPlaylistAcceptsMasterPlaylist()
    {
        var playlist = HLS.ParseMasterPlaylist(MasterPlaylist, SourceUrl);

        Assert.AreEqual(2, playlist.VariantPlaylistsRefs.Count);
        Assert.AreEqual("https://example.com/720p.m3u8", playlist.VariantPlaylistsRefs[0].Url);
        Assert.AreEqual("https://example.com/480p.m3u8", playlist.VariantPlaylistsRefs[1].Url);
        Assert.IsTrue(playlist.IndependentSegments);
    }

    [TestMethod]
    public void TestIsMediaPlaylist()
    {
        Assert.IsTrue(HLS.IsMediaPlaylist(MediaPlaylist));
        Assert.IsFalse(HLS.IsMediaPlaylist(MasterPlaylist));
    }

    [TestMethod]
    public void TestIsMediaPlaylistTargetDurationOnly()
    {
        Assert.IsTrue(HLS.IsMediaPlaylist("#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-TARGETDURATION:10\n"));
    }

    [TestMethod]
    public void TestIsMediaPlaylistCarriageReturns()
    {
        Assert.IsTrue(HLS.IsMediaPlaylist(MediaPlaylist.Replace("\n", "\r\n")));
        Assert.IsFalse(HLS.IsMediaPlaylist(MasterPlaylist.Replace("\n", "\r\n")));
    }

    [TestMethod]
    public void TestVariantPlaylistRoundTripPreservesMapAndSegments()
    {
        var playlist = HLS.ParseVariantPlaylist(MediaPlaylist, SourceUrl);

        Assert.AreEqual(10, playlist.TargetDuration);
        Assert.AreEqual("https://cf-media.sndcdn.com/init.mp4", playlist.MapUrl);
        Assert.AreEqual(2, playlist.Segments.OfType<HLS.MediaSegment>().Count());

        var generated = playlist.GenerateM3U8();
        StringAssert.Contains(generated, "#EXT-X-TARGETDURATION:10");
        StringAssert.Contains(generated, "#EXT-X-MAP:URI=\"https://cf-media.sndcdn.com/init.mp4\"");
        StringAssert.Contains(generated, "https://cf-media.sndcdn.com/segment0.m4s");
        StringAssert.Contains(generated, "https://cf-media.sndcdn.com/segment1.m4s");
        Assert.AreEqual(2, generated.Split('\n').Count(line => line.StartsWith("#EXTINF:")));

        Assert.IsTrue(HLS.IsMediaPlaylist(generated));
    }

    private const string MultiKeyPlaylist = """
    #EXTM3U
    #EXT-X-VERSION:3
    #EXT-X-TARGETDURATION:10
    #EXT-X-KEY:METHOD=AES-128,URI="https://keys.example.com/key1",IV=0x00000000000000000000000000000001
    #EXTINF:9.0,
    https://cdn.example.com/segment0.ts
    #EXT-X-KEY:METHOD=AES-128,URI="https://keys.example.com/key2",IV=0x00000000000000000000000000000002
    #EXTINF:9.0,
    https://cdn.example.com/segment1.ts
    #EXT-X-ENDLIST
    """;

    private const string MethodNonePlaylist = """
    #EXTM3U
    #EXT-X-VERSION:3
    #EXT-X-TARGETDURATION:10
    #EXT-X-KEY:METHOD=AES-128,URI="https://keys.example.com/key1"
    #EXTINF:9.0,
    https://cdn.example.com/enc0.ts
    #EXT-X-KEY:METHOD=NONE
    #EXTINF:9.0,
    https://cdn.example.com/clear0.ts
    #EXT-X-ENDLIST
    """;

    private const string MultiDrmPlaylist = """
    #EXTM3U
    #EXT-X-VERSION:6
    #EXT-X-TARGETDURATION:10
    #EXT-X-KEY:METHOD=AES-128,URI="https://drm.example.com/widevine",KEYFORMAT="urn:uuid:edef8ba9-79d6-4ace-a3c8-27dcd51d21ed",KEYFORMATVERSIONS="1"
    #EXT-X-KEY:METHOD=AES-128,URI="https://keys.example.com/clearkey"
    #EXTINF:9.0,
    https://cdn.example.com/segment0.ts
    #EXT-X-ENDLIST
    """;

    private const string DrmOnlyPlaylist = """
    #EXTM3U
    #EXT-X-VERSION:6
    #EXT-X-TARGETDURATION:10
    #EXT-X-KEY:METHOD=SAMPLE-AES,URI="https://drm.example.com/fairplay",KEYFORMAT="com.apple.streamingkeydelivery"
    #EXT-X-KEY:METHOD=AES-128,URI="https://drm.example.com/widevine",KEYFORMAT="urn:uuid:edef8ba9-79d6-4ace-a3c8-27dcd51d21ed"
    #EXTINF:9.0,
    https://cdn.example.com/segment0.ts
    #EXT-X-ENDLIST
    """;

    private const string SampleAesPlaylist = """
    #EXTM3U
    #EXT-X-VERSION:5
    #EXT-X-TARGETDURATION:10
    #EXT-X-KEY:METHOD=SAMPLE-AES,URI="https://drm.example.com/fairplay",KEYFORMAT="com.apple.streamingkeydelivery"
    #EXTINF:9.0,
    https://cdn.example.com/segment0.ts
    #EXT-X-ENDLIST
    """;

    private const string KeyBeforeMapPlaylist = """
    #EXTM3U
    #EXT-X-VERSION:6
    #EXT-X-TARGETDURATION:10
    #EXT-X-KEY:METHOD=AES-128,URI="https://keys.example.com/key1",IV=0x00000000000000000000000000000001
    #EXT-X-MAP:URI="https://cf-media.sndcdn.com/init.mp4"
    #EXTINF:9.0,
    https://cf-media.sndcdn.com/segment0.m4s
    #EXTINF:9.0,
    https://cf-media.sndcdn.com/segment1.m4s
    #EXT-X-ENDLIST
    """;

    private const string MapBeforeKeyPlaylist = """
    #EXTM3U
    #EXT-X-VERSION:6
    #EXT-X-TARGETDURATION:10
    #EXT-X-MAP:URI="https://cf-media.sndcdn.com/init.mp4"
    #EXT-X-KEY:METHOD=AES-128,URI="https://keys.example.com/key1",IV=0x00000000000000000000000000000001
    #EXTINF:9.0,
    https://cf-media.sndcdn.com/segment0.m4s
    #EXTINF:9.0,
    https://cf-media.sndcdn.com/segment1.m4s
    #EXT-X-ENDLIST
    """;

    private const string KeyRotationAfterMapPlaylist = """
    #EXTM3U
    #EXT-X-VERSION:6
    #EXT-X-TARGETDURATION:10
    #EXT-X-KEY:METHOD=AES-128,URI="https://keys.example.com/key1",IV=0x00000000000000000000000000000001
    #EXT-X-MAP:URI="https://cf-media.sndcdn.com/init.mp4"
    #EXT-X-KEY:METHOD=AES-128,URI="https://keys.example.com/key2",IV=0x00000000000000000000000000000002
    #EXTINF:9.0,
    https://cf-media.sndcdn.com/segment0.m4s
    #EXTINF:9.0,
    https://cf-media.sndcdn.com/segment1.m4s
    #EXT-X-ENDLIST
    """;

    [TestMethod]
    public void TestVariantPlaylistPositionalKeysRoundTrip()
    {
        var playlist = HLS.ParseVariantPlaylist(MultiKeyPlaylist, SourceUrl);
        var segments = playlist.Segments.OfType<HLS.MediaSegment>().ToList();

        Assert.AreEqual(2, segments.Count);
        Assert.AreEqual("https://keys.example.com/key1", segments[0].Keys?.Single().KeyUrl);
        Assert.AreEqual("https://keys.example.com/key2", segments[1].Keys?.Single().KeyUrl);

        var generated = playlist.GenerateM3U8();
        var key1Index = generated.IndexOf("https://keys.example.com/key1");
        var segment0Index = generated.IndexOf("https://cdn.example.com/segment0.ts");
        var key2Index = generated.IndexOf("https://keys.example.com/key2");
        var segment1Index = generated.IndexOf("https://cdn.example.com/segment1.ts");
        Assert.IsTrue(key1Index >= 0 && key1Index < segment0Index);
        Assert.IsTrue(key2Index > segment0Index && key2Index < segment1Index);
        Assert.AreEqual(2, generated.Split('\n').Count(line => line.StartsWith("#EXT-X-KEY:")));
    }

    [TestMethod]
    public void TestVariantPlaylistMethodNoneTransition()
    {
        var playlist = HLS.ParseVariantPlaylist(MethodNonePlaylist, SourceUrl);
        var segments = playlist.Segments.OfType<HLS.MediaSegment>().ToList();

        Assert.AreEqual(2, segments.Count);
        Assert.IsTrue(segments[0].Keys?.Single().IsEncrypted);
        Assert.IsFalse(segments[1].Keys?.Single().IsEncrypted);

        var generated = playlist.GenerateM3U8();
        var noneIndex = generated.IndexOf("#EXT-X-KEY:METHOD=NONE");
        var encryptedIndex = generated.IndexOf("https://cdn.example.com/enc0.ts");
        var clearIndex = generated.IndexOf("https://cdn.example.com/clear0.ts");
        Assert.IsTrue(noneIndex > encryptedIndex && noneIndex < clearIndex);
    }

    [TestMethod]
    public void TestDecryptionSelectsIdentityKeyNextToDrmKey()
    {
        var playlist = HLS.ParseVariantPlaylist(MultiDrmPlaylist, SourceUrl);
        var segment = playlist.Segments.OfType<HLS.MediaSegment>().Single();

        Assert.AreEqual(2, segment.Keys?.Count);
        Assert.AreEqual("https://keys.example.com/clearkey", playlist.Decryption?.KeyUrl);
    }

    [TestMethod]
    public void TestDecryptionIgnoresDrmOnlyKeys()
    {
        var playlist = HLS.ParseVariantPlaylist(DrmOnlyPlaylist, SourceUrl);

        Assert.IsNull(playlist.Decryption);
    }

    [TestMethod]
    public void TestDecryptionLastIdentityKeyWins()
    {
        var playlist = HLS.ParseVariantPlaylist(MultiKeyPlaylist, SourceUrl);

        Assert.AreEqual("https://keys.example.com/key2", playlist.Decryption?.KeyUrl);
    }

    [TestMethod]
    public void TestVariantPlaylistPreservesKeyBeforeMapOrder()
    {
        var playlist = HLS.ParseVariantPlaylist(KeyBeforeMapPlaylist, SourceUrl);
        Assert.IsFalse(playlist.MapBeforeKey);

        var generated = playlist.GenerateM3U8();
        var keyIndex = generated.IndexOf("#EXT-X-KEY:");
        var mapIndex = generated.IndexOf("#EXT-X-MAP:");

        Assert.IsTrue(keyIndex >= 0 && mapIndex >= 0 && keyIndex < mapIndex, "KEY line must precede MAP line");
        Assert.AreEqual(1, CountOccurrences(generated, "#EXT-X-KEY:"));
        StringAssert.Contains(generated, "https://cf-media.sndcdn.com/segment0.m4s");
        StringAssert.Contains(generated, "https://cf-media.sndcdn.com/segment1.m4s");
    }

    [TestMethod]
    public void TestVariantPlaylistPreservesMapBeforeKeyOrder()
    {
        var playlist = HLS.ParseVariantPlaylist(MapBeforeKeyPlaylist, SourceUrl);
        Assert.IsTrue(playlist.MapBeforeKey);

        var generated = playlist.GenerateM3U8();
        var keyIndex = generated.IndexOf("#EXT-X-KEY:");
        var mapIndex = generated.IndexOf("#EXT-X-MAP:");

        Assert.IsTrue(keyIndex >= 0 && mapIndex >= 0 && mapIndex < keyIndex, "MAP line must precede KEY line");
        Assert.AreEqual(1, CountOccurrences(generated, "#EXT-X-KEY:"));
        StringAssert.Contains(generated, "https://cf-media.sndcdn.com/segment0.m4s");
        StringAssert.Contains(generated, "https://cf-media.sndcdn.com/segment1.m4s");
    }

    [TestMethod]
    public void TestUnsupportedKeyForSampleAes()
    {
        var playlist = HLS.ParseVariantPlaylist(SampleAesPlaylist, SourceUrl);
        var unsupportedKey = playlist.FindUnsupportedKey();

        Assert.IsNotNull(unsupportedKey);
        Assert.AreEqual("SAMPLE-AES", unsupportedKey.Method);
        Assert.IsNull(playlist.Decryption);
    }

    [TestMethod]
    public void TestUnsupportedKeyForDrmOnly()
    {
        var playlist = HLS.ParseVariantPlaylist(DrmOnlyPlaylist, SourceUrl);

        Assert.IsNotNull(playlist.FindUnsupportedKey());
    }

    [TestMethod]
    public void TestUnsupportedKeyNullWhenIdentityKeyPresent()
    {
        var multiDrm = HLS.ParseVariantPlaylist(MultiDrmPlaylist, SourceUrl);
        var methodNone = HLS.ParseVariantPlaylist(MethodNonePlaylist, SourceUrl);

        Assert.IsNull(multiDrm.FindUnsupportedKey());
        Assert.IsNull(methodNone.FindUnsupportedKey());
    }

    [TestMethod]
    public void TestSegmentDecryptionFollowsKeyRotation()
    {
        var playlist = HLS.ParseVariantPlaylist(MultiKeyPlaylist, SourceUrl);
        var segments = playlist.Segments.OfType<HLS.MediaSegment>().ToList();

        Assert.AreEqual(2, segments.Count);
        Assert.AreEqual("https://keys.example.com/key1", playlist.GetSegmentDecryption(segments[0])?.KeyUrl);
        Assert.AreEqual("https://keys.example.com/key2", playlist.GetSegmentDecryption(segments[1])?.KeyUrl);
    }

    [TestMethod]
    public void TestSegmentDecryptionNullAfterMethodNone()
    {
        var playlist = HLS.ParseVariantPlaylist(MethodNonePlaylist, SourceUrl);
        var segments = playlist.Segments.OfType<HLS.MediaSegment>().ToList();

        Assert.AreEqual(2, segments.Count);
        Assert.AreEqual("https://keys.example.com/key1", playlist.GetSegmentDecryption(segments[0])?.KeyUrl);
        Assert.IsNull(playlist.GetSegmentDecryption(segments[1]));
    }

    [TestMethod]
    public void TestMapDecryptionFollowsKeyOrder()
    {
        var keyBeforeMap = HLS.ParseVariantPlaylist(KeyBeforeMapPlaylist, SourceUrl);
        var mapBeforeKey = HLS.ParseVariantPlaylist(MapBeforeKeyPlaylist, SourceUrl);

        Assert.AreEqual("https://keys.example.com/key1", keyBeforeMap.GetMapDecryption()?.KeyUrl);
        Assert.IsNull(mapBeforeKey.GetMapDecryption());
    }

    [TestMethod]
    public void TestMapDecryptionUsesKeyActiveAtMap()
    {
        var playlist = HLS.ParseVariantPlaylist(KeyRotationAfterMapPlaylist, SourceUrl);
        var segments = playlist.Segments.OfType<HLS.MediaSegment>().ToList();

        Assert.AreEqual("https://keys.example.com/key1", playlist.GetMapDecryption()?.KeyUrl);
        Assert.AreEqual("https://keys.example.com/key2", playlist.GetSegmentDecryption(segments[0])?.KeyUrl);

        var generated = playlist.GenerateM3U8();
        var key1Index = generated.IndexOf("https://keys.example.com/key1");
        var mapIndex = generated.IndexOf("#EXT-X-MAP:");
        var key2Index = generated.IndexOf("https://keys.example.com/key2");
        var segment0Index = generated.IndexOf("https://cf-media.sndcdn.com/segment0.m4s");
        Assert.IsTrue(key1Index >= 0 && key1Index < mapIndex);
        Assert.IsTrue(key2Index > mapIndex && key2Index < segment0Index);
        Assert.AreEqual(2, generated.Split('\n').Count(line => line.StartsWith("#EXT-X-KEY:")));

        var reparsed = HLS.ParseVariantPlaylist(generated, SourceUrl);
        Assert.AreEqual("https://keys.example.com/key1", reparsed.GetMapDecryption()?.KeyUrl);
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var position = text.IndexOf(value, StringComparison.Ordinal);
        while (position >= 0)
        {
            count++;
            position = text.IndexOf(value, position + value.Length, StringComparison.Ordinal);
        }
        return count;
    }
}
