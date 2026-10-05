using System.Buffers.Binary;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Xml.Linq;
using Grayjay.ClientServer.Controllers;
using Grayjay.ClientServer.Exceptions;
using Grayjay.ClientServer.Helpers;
using Grayjay.Engine.Models.Video.Sources;

namespace Grayjay.Desktop.Tests;

[TestClass]
public class Mp4MetadataHelperTests
{
    private static readonly XNamespace DashNamespace = "urn:mpeg:dash:schema:mpd:2011";
    private static readonly XNamespace CencNamespace = "urn:mpeg:cenc:2013";
    private static readonly byte[] WidevineSystemId = Convert.FromHexString("edef8ba979d64acea3c827dcd51d21ed");

    private static byte[] Box(string type, int size)
    {
        var box = new byte[size];
        BinaryPrimitives.WriteUInt32BigEndian(box.AsSpan(0, 4), (uint)size);
        Encoding.ASCII.GetBytes(type).CopyTo(box, 4);
        return box;
    }

    private static byte[] LargeBoxHeader(string type, ulong size)
    {
        var header = new byte[16];
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(0, 4), 1);
        Encoding.ASCII.GetBytes(type).CopyTo(header, 4);
        BinaryPrimitives.WriteUInt64BigEndian(header.AsSpan(8, 8), size);
        return header;
    }

    private static Func<long, int, byte[]?> Reader(Dictionary<long, byte[]> boxesByOffset) =>
        (offset, count) => boxesByOffset.TryGetValue(offset, out var bytes) ? bytes.Take(count).ToArray() : null;

    [TestMethod]
    public void FindOnDemandRanges_ReadsMoovAndSidx()
    {
        var reader = Reader(new Dictionary<long, byte[]>()
        {
            { 0, Box("ftyp", 24) },
            { 24, Box("moov", 1000) },
            { 1024, Box("sidx", 52) }
        });

        var metaData = Mp4MetadataHelper.FindOnDemandRanges(reader);

        Assert.IsNotNull(metaData);
        Assert.AreEqual(0, metaData.FileInitStart);
        Assert.AreEqual(1023, metaData.FileInitEnd);
        Assert.AreEqual(1024, metaData.FileIndexStart);
        Assert.AreEqual(1075, metaData.FileIndexEnd);
    }

    [TestMethod]
    public void FindOnDemandRanges_RejectsBoxSizeThatOverflowsTheOffset()
    {
        var reader = Reader(new Dictionary<long, byte[]>()
        {
            { 0, Box("ftyp", 100) },
            { 100, LargeBoxHeader("moov", long.MaxValue) }
        });

        Assert.IsNull(Mp4MetadataHelper.FindOnDemandRanges(reader));
    }

    [TestMethod]
    public void FindOnDemandRanges_StopsWhenNextOffsetWouldOverflow()
    {
        var reader = Reader(new Dictionary<long, byte[]>()
        {
            { 0, Box("moov", 100) },
            { 100, LargeBoxHeader("free", long.MaxValue) }
        });

        var metaData = Mp4MetadataHelper.FindOnDemandRanges(reader);

        Assert.IsNotNull(metaData);
        Assert.AreEqual(99, metaData.FileInitEnd);
        Assert.IsNull(metaData.FileIndexStart);
    }

    [TestMethod]
    public void WrapBareWidevinePsshData_WrapsBareData()
    {
        var data = new byte[] { 0x08, 0x01, 0x12, 0x10 };

        var box = Mp4MetadataHelper.WrapBareWidevinePsshData(data);

        Assert.IsNotNull(box);
        Assert.AreEqual(32 + data.Length, box.Length);
        Assert.AreEqual("pssh", Encoding.ASCII.GetString(box, 4, 4));
        CollectionAssert.AreEqual(WidevineSystemId, box.Skip(12).Take(16).ToArray());
        CollectionAssert.AreEqual(data, box.Skip(32).ToArray());
    }

    [TestMethod]
    public void WrapBareWidevinePsshData_LeavesBoxUnchanged()
    {
        var box = Mp4MetadataHelper.WrapBareWidevinePsshData(new byte[] { 0x08, 0x01 });

        Assert.IsNull(Mp4MetadataHelper.WrapBareWidevinePsshData(box!));
    }

    [TestMethod]
    public void WrapBareWidevinePsshData_DoesNotWrapEmptyData()
    {
        Assert.IsNull(Mp4MetadataHelper.WrapBareWidevinePsshData(Array.Empty<byte>()));
    }

    private static XDocument Manifest(params string[] psshValues)
    {
        var contentProtection = new XElement(DashNamespace + "ContentProtection",
            new XAttribute("schemeIdUri", "urn:uuid:edef8ba9-79d6-4ace-a3c8-27dcd51d21ed"),
            psshValues.Select(value => new XElement(CencNamespace + "pssh", value)));
        return new XDocument(new XElement(DashNamespace + "MPD",
            new XAttribute(XNamespace.Xmlns + "cenc", CencNamespace.NamespaceName),
            new XElement(DashNamespace + "Period", new XElement(DashNamespace + "AdaptationSet", contentProtection))));
    }

    [TestMethod]
    public void WrapBareWidevinePssh_RemovesEmptyPssh()
    {
        var bareData = Convert.ToBase64String(new byte[] { 0x08, 0x01, 0x12, 0x10 });
        var document = Manifest("", "  ", bareData);

        DetailsController.WrapBareWidevinePssh(document);

        var psshValues = document.Descendants(CencNamespace + "pssh").Select(element => element.Value).ToList();
        Assert.AreEqual(1, psshValues.Count);
        Assert.AreEqual("pssh", Encoding.ASCII.GetString(Convert.FromBase64String(psshValues[0]), 4, 4));
    }

    private static byte[] OnDemandFile(int moovSize)
    {
        return Box("ftyp", 24).Concat(Box("moov", moovSize)).Concat(Box("sidx", 52)).Concat(Box("mdat", 100)).ToArray();
    }

    private static StreamMetaData FetchMp4Metadata(string url)
    {
        var method = typeof(DetailsController).GetMethod("FetchMp4Metadata", BindingFlags.NonPublic | BindingFlags.Static)!;
        try
        {
            var ranges = ((ITuple)method.Invoke(null, new object?[] { url, null })!)[0]!;
            int ReadRange(string name) => (int)ranges.GetType().GetProperty(name)!.GetValue(ranges)!;
            return new StreamMetaData()
            {
                FileInitStart = ReadRange("InitStart"),
                FileInitEnd = ReadRange("InitEnd"),
                FileIndexStart = ReadRange("IndexStart"),
                FileIndexEnd = ReadRange("IndexEnd")
            };
        }
        catch (TargetInvocationException ex) when (ex.InnerException != null)
        {
            throw ex.InnerException;
        }
    }

    private static byte[] RangeResponse(byte[] file, LoopbackRequest request, bool honorStart)
    {
        var range = request.Header("Range")!.Substring("bytes=".Length).Split('-');
        long start = honorStart ? long.Parse(range[0]) : 0;
        long end = Math.Min(long.Parse(range[1]) - long.Parse(range[0]) + start, file.Length - 1);
        var body = file.Skip((int)start).Take((int)(end - start + 1)).ToArray();
        var header = $"HTTP/1.1 206 Partial Content\r\nContent-Range: bytes {start}-{end}/{file.Length}\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n";
        return Encoding.ASCII.GetBytes(header).Concat(body).ToArray();
    }

    [TestMethod]
    public void FetchMp4Metadata_ReadsRangesAcrossReadAheadWindows()
    {
        var file = OnDemandFile(100_000);
        using var server = new LoopbackServer(request => RangeResponse(file, request, honorStart: true));

        var metaData = FetchMp4Metadata(server.Url);

        Assert.AreEqual(100_023, metaData.FileInitEnd);
        Assert.AreEqual(100_024, metaData.FileIndexStart);
        Assert.AreEqual(100_075, metaData.FileIndexEnd);
    }

    [TestMethod]
    public void FetchMp4Metadata_RangeFromWrongOffsetEndsInDialog()
    {
        var file = OnDemandFile(100_000);
        using var server = new LoopbackServer(request => RangeResponse(file, request, honorStart: false));

        Assert.ThrowsException<DialogException>(() => FetchMp4Metadata(server.Url));
    }

    [TestMethod]
    public void FetchMp4Metadata_ReportsHttpStatus()
    {
        var body = Encoding.ASCII.GetBytes("forbidden");
        var header = $"HTTP/1.1 403 Forbidden\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n";
        using var server = new LoopbackServer(Encoding.ASCII.GetBytes(header).Concat(body).ToArray());

        var exception = Assert.ThrowsException<DialogException>(() => FetchMp4Metadata(server.Url));

        StringAssert.Contains(exception.Model.Message, "HTTP 403");
    }
}
