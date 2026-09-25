using System.Net;
using System.Net.Sockets;
using System.Text;
using Grayjay.ClientServer;
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

    private static byte[] Concat(byte[] first, byte[] second)
    {
        var combined = new byte[first.Length + second.Length];
        Buffer.BlockCopy(first, 0, combined, 0, first.Length);
        Buffer.BlockCopy(second, 0, combined, first.Length, second.Length);
        return combined;
    }

    private sealed class LoopbackServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cancellation = new();
        private readonly Task _serverTask;

        public string Url { get; }

        public LoopbackServer(byte[] response)
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            Url = $"http://127.0.0.1:{port}/";
            _serverTask = Task.Run(() => ServeAsync(response, _cancellation.Token));
        }

        private async Task ServeAsync(byte[] response, CancellationToken cancellationToken)
        {
            try
            {
                using var client = await _listener.AcceptTcpClientAsync(cancellationToken);
                using var stream = client.GetStream();
                var requestBuffer = new byte[4096];
                var requestBytes = new List<byte>();
                while (!cancellationToken.IsCancellationRequested)
                {
                    int read = await stream.ReadAsync(requestBuffer, cancellationToken);
                    if (read <= 0)
                        return;
                    requestBytes.AddRange(requestBuffer.Take(read));
                    if (Encoding.ASCII.GetString(requestBytes.ToArray()).Contains("\r\n\r\n"))
                        break;
                }

                await stream.WriteAsync(response, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
            catch (Exception)
            {
            }
        }

        public void Dispose()
        {
            _cancellation.Cancel();
            _listener.Stop();
            try
            {
                _serverTask.Wait(TimeSpan.FromSeconds(2));
            }
            catch (Exception)
            {
            }
            _cancellation.Dispose();
        }
    }
}
