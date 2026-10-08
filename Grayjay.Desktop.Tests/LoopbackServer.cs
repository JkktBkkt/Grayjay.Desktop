using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Grayjay.Desktop.Tests;

internal sealed record LoopbackRequest(string Method, string Path, IReadOnlyList<string> HeaderLines, byte[] Body)
{
    public string? Header(string name)
    {
        var prefix = name + ":";
        var line = HeaderLines.FirstOrDefault(headerLine => headerLine.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        return line?.Substring(prefix.Length).Trim();
    }
}

internal sealed class LoopbackServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Task _serverTask;

    public string Url { get; }

    public LoopbackServer(byte[] response) : this(_ => response)
    {
    }

    public LoopbackServer(Func<LoopbackRequest, byte[]> handler)
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        Url = $"http://127.0.0.1:{port}/";
        _serverTask = Task.Run(() => ServeAsync(handler, _cancellation.Token));
    }

    private async Task ServeAsync(Func<LoopbackRequest, byte[]> handler, CancellationToken cancellationToken)
    {
        while (true)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(cancellationToken);
            }
            catch (Exception) when (cancellationToken.IsCancellationRequested)
            {
                // Dispose stops the listener, which ends the accept loop.
                return;
            }

            using (client)
            {
                await HandleAsync(client, handler, cancellationToken);
            }
        }
    }

    private static async Task HandleAsync(TcpClient client, Func<LoopbackRequest, byte[]> handler, CancellationToken cancellationToken)
    {
        try
        {
            using var stream = client.GetStream();
            var requestBuffer = new byte[4096];
            var requestBytes = new List<byte>();
            int headerEnd;
            while ((headerEnd = IndexOfHeaderEnd(requestBytes)) < 0)
            {
                int read = await stream.ReadAsync(requestBuffer, cancellationToken);
                if (read <= 0)
                    return;
                requestBytes.AddRange(requestBuffer.Take(read));
            }

            var headerLines = Encoding.ASCII.GetString(requestBytes.Take(headerEnd).ToArray()).Split("\r\n");
            var requestLine = headerLines[0].Split(' ');
            var contentLengthLine = headerLines.FirstOrDefault(line => line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
            var contentLength = (contentLengthLine != null) ? int.Parse(contentLengthLine.Substring("Content-Length:".Length).Trim()) : 0;
            var bodyStart = headerEnd + 4;
            while (requestBytes.Count < bodyStart + contentLength)
            {
                int read = await stream.ReadAsync(requestBuffer, cancellationToken);
                if (read <= 0)
                    return;
                requestBytes.AddRange(requestBuffer.Take(read));
            }

            var request = new LoopbackRequest(requestLine[0], requestLine[1], headerLines.Skip(1).ToList(), requestBytes.Skip(bodyStart).Take(contentLength).ToArray());
            await stream.WriteAsync(handler(request), cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }
        catch (IOException)
        {
            // The client may close early, as the bounded read does when Content-Length is over the cap.
        }
    }

    private static int IndexOfHeaderEnd(List<byte> bytes)
    {
        for (int index = 0; index + 3 < bytes.Count; index++)
        {
            if (bytes[index] == '\r' && bytes[index + 1] == '\n' && bytes[index + 2] == '\r' && bytes[index + 3] == '\n')
                return index;
        }
        return -1;
    }

    public void Dispose()
    {
        _cancellation.Cancel();
        _listener.Stop();
        Assert.IsTrue(_serverTask.Wait(TimeSpan.FromSeconds(2)), "The loopback server did not stop.");
        _cancellation.Dispose();
    }
}
