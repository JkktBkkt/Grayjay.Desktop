using System.IO.Compression;
using System.Text;
using Grayjay.ClientServer;
using Grayjay.Engine.Models;
using Grayjay.Engine.Web;

namespace Grayjay.Desktop.Tests;

[TestClass]
public class ModifierHttpTests
{
    private const long ByteCap = 100;

    [TestMethod]
    public void TestBoundedReadShortCircuitsOnContentLength()
    {
        var body = new byte[1000];
        Array.Fill(body, (byte)'a');
        var header = "HTTP/1.1 200 OK\r\nContent-Type: application/octet-stream\r\nContent-Length: 1000\r\nConnection: close\r\n\r\n";

        using var server = new LoopbackServer(Concat(Encoding.ASCII.GetBytes(header), body));
        var result = ModifierHttp.GetBytesBounded(new ManagedHttpClient(), server.Url, null, null, ByteCap);

        Assert.IsTrue(result.LimitExceeded);
        Assert.AreEqual(200, result.Code);
        Assert.AreEqual(0, result.Bytes.Length);
    }

    [TestMethod]
    public void TestBoundedReadStopsOnChunkedOverflow()
    {
        var chunkBody = new string('b', 300);
        var response = "HTTP/1.1 200 OK\r\nContent-Type: application/octet-stream\r\nTransfer-Encoding: chunked\r\nConnection: close\r\n\r\n"
            + chunkBody.Length.ToString("x") + "\r\n" + chunkBody + "\r\n0\r\n\r\n";

        using var server = new LoopbackServer(Encoding.ASCII.GetBytes(response));
        var result = ModifierHttp.GetBytesBounded(new ManagedHttpClient(), server.Url, null, null, ByteCap);

        Assert.IsTrue(result.LimitExceeded);
        Assert.AreEqual(0, result.Bytes.Length);
    }

    [TestMethod]
    public void TestBoundedReadHandlesContentLengthAboveIntRange()
    {
        var body = Encoding.ASCII.GetBytes(new string('d', 200));
        var header = "HTTP/1.1 200 OK\r\nContent-Type: application/octet-stream\r\nContent-Length: 3000000000\r\nConnection: close\r\n\r\n";

        using var server = new LoopbackServer(Concat(Encoding.ASCII.GetBytes(header), body));
        var result = ModifierHttp.GetBytesBounded(new ManagedHttpClient(), server.Url, null, null, ByteCap);

        Assert.IsTrue(result.LimitExceeded);
        Assert.AreEqual(200, result.Code);
        Assert.AreEqual(0, result.Bytes.Length);
    }

    [TestMethod]
    public void TestBoundedReadReturnsBodyUnderCap()
    {
        var body = Encoding.ASCII.GetBytes(new string('c', 50));
        var header = "HTTP/1.1 200 OK\r\nContent-Type: application/octet-stream\r\nContent-Length: 50\r\nConnection: close\r\n\r\n";

        using var server = new LoopbackServer(Concat(Encoding.ASCII.GetBytes(header), body));
        var result = ModifierHttp.GetBytesBounded(new ManagedHttpClient(), server.Url, null, null, ByteCap);

        Assert.IsFalse(result.LimitExceeded);
        Assert.IsTrue(result.IsOk);
        CollectionAssert.AreEqual(body, result.Bytes);
    }

    [TestMethod]
    public void TestBoundedReadAcceptsBodyExactlyAtCap()
    {
        var body = Encoding.ASCII.GetBytes(new string('e', (int)ByteCap));
        var header = $"HTTP/1.1 200 OK\r\nContent-Type: application/octet-stream\r\nContent-Length: {ByteCap}\r\nConnection: close\r\n\r\n";

        using var server = new LoopbackServer(Concat(Encoding.ASCII.GetBytes(header), body));
        var result = ModifierHttp.GetBytesBounded(new ManagedHttpClient(), server.Url, null, null, ByteCap);

        Assert.IsFalse(result.LimitExceeded);
        CollectionAssert.AreEqual(body, result.Bytes);
    }

    [TestMethod]
    public void TestBoundedReadRejectsOneByteOverCapWithoutContentLength()
    {
        var body = Encoding.ASCII.GetBytes(new string('f', (int)ByteCap + 1));
        var header = "HTTP/1.1 200 OK\r\nContent-Type: application/octet-stream\r\nConnection: close\r\n\r\n";

        using var server = new LoopbackServer(Concat(Encoding.ASCII.GetBytes(header), body));
        var result = ModifierHttp.GetBytesBounded(new ManagedHttpClient(), server.Url, null, null, ByteCap);

        Assert.IsTrue(result.LimitExceeded);
        Assert.AreEqual(0, result.Bytes.Length);
    }

    [TestMethod]
    public void TestGetStreamReportsZeroWithoutContentLength()
    {
        var header = "HTTP/1.1 200 OK\r\nContent-Type: application/octet-stream\r\nConnection: close\r\n\r\n";

        using var server = new LoopbackServer(Concat(Encoding.ASCII.GetBytes(header), Encoding.ASCII.GetBytes("body")));
        var result = ModifierHttp.GetStream(new ManagedHttpClient(), server.Url);
        using var stream = result.Stream;

        Assert.AreEqual(0, result.ContentLength);
    }

    [TestMethod]
    public void TestPostBytesSendsBody()
    {
        var challenge = new byte[] { 0x08, 0x01, 0x12, 0x00, 0xFF };
        using var server = new LoopbackServer(request =>
        {
            var echo = Concat(Encoding.ASCII.GetBytes(request.Method + ":"), request.Body);
            return Concat(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: {echo.Length}\r\nConnection: close\r\n\r\n"), echo);
        });
        var result = ModifierHttp.PostBytes(new ManagedHttpClient(), server.Url, challenge);

        Assert.IsTrue(result.IsOk);
        CollectionAssert.AreEqual(Concat(Encoding.ASCII.GetBytes("POST:"), challenge), result.Bytes);
    }

    [TestMethod]
    public void TestPostBytesDecodesGzipLicense()
    {
        AssertPostBytesDecodes("gzip", input => new GZipStream(input, CompressionLevel.Optimal, leaveOpen: true));
    }

    [TestMethod]
    public void TestPostBytesDecodesBrotliLicense()
    {
        AssertPostBytesDecodes("br", input => new BrotliStream(input, CompressionLevel.Optimal, leaveOpen: true));
    }

    private static void AssertPostBytesDecodes(string encoding, Func<Stream, Stream> createEncoder)
    {
        var license = Encoding.ASCII.GetBytes(new string('l', 500));
        var compressed = Compress(license, createEncoder);
        var header = $"HTTP/1.1 200 OK\r\nContent-Type: application/octet-stream\r\nContent-Encoding: {encoding}\r\nContent-Length: {compressed.Length}\r\nConnection: close\r\n\r\n";

        using var server = new LoopbackServer(Concat(Encoding.ASCII.GetBytes(header), compressed));
        // Plugins that copy browser headers ask for br as well.
        var headers = new HttpHeaders() { { "Accept-Encoding", "gzip, deflate, br" } };
        var result = ModifierHttp.PostBytes(new ManagedHttpClient(), server.Url, new byte[] { 1, 2, 3 }, null, headers);

        Assert.IsTrue(result.IsOk);
        CollectionAssert.AreEqual(license, result.Bytes);
    }

    private static byte[] Compress(byte[] data, Func<Stream, Stream> createEncoder)
    {
        using var output = new MemoryStream();
        using (var encoder = createEncoder(output))
        {
            encoder.Write(data);
        }
        return output.ToArray();
    }

    private static byte[] Concat(byte[] first, byte[] second)
    {
        var combined = new byte[first.Length + second.Length];
        Buffer.BlockCopy(first, 0, combined, 0, first.Length);
        Buffer.BlockCopy(second, 0, combined, first.Length, second.Length);
        return combined;
    }
}
