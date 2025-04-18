using System.Net;
using System.Net.Sockets;
using System.Text;
using global::Grayjay.ClientServer.Proxy;

namespace Grayjay.Desktop.Tests;

[TestClass]
public class RelativeProxyTests
{
    private TcpListener? _listener;
    private HttpRelativeProxy? _relativeProxy;
    private CancellationTokenSource? _serverCancellationTokenSource;

    [TestInitialize]
    public void Setup()
    {
        _serverCancellationTokenSource = new CancellationTokenSource();
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        var mockServerPort = ((IPEndPoint)_listener.LocalEndpoint).Port;

        _relativeProxy = new HttpRelativeProxy(
            new IPEndPoint(IPAddress.Loopback, 0),
            $"http://localhost:{mockServerPort}/api/",
            new RequestHeaderOptions(),
            new ResponseHeaderOptions(),
            null,
            new[] { "GET", "POST" },
            true
        );
        _relativeProxy.Start();
    }

    [TestCleanup]
    public void Cleanup()
    {
        _relativeProxy?.Dispose();
        _serverCancellationTokenSource?.Cancel();
        _listener?.Stop();
    }

    [TestMethod]
    public async Task TestRelativeProxy_BasicRequestHandling()
    {
        // Arrange
        var responseGenerator = new Func<HttpProxyRequest, byte[]>(req => Encoding.UTF8.GetBytes($"Received request for path: {req.Path}"));
        var serverTask = StartMockServer(_listener!, responseGenerator, _serverCancellationTokenSource!.Token);

        // Act
        var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)_relativeProxy!.LocalEndPoint).Port);
        var clientStream = client.GetStream();

        var request = "GET /users/123 HTTP/1.1\r\nHost: localhost\r\n\r\n";
        await clientStream.WriteAsync(Encoding.UTF8.GetBytes(request));
        await clientStream.FlushAsync();

        using var httpStream = new HttpProxyStream(clientStream);
        var response = await httpStream.ReadResponseHeadersAsync();
        var bodyStream = new MemoryStream();
        await httpStream.TransferUntilEndOfStreamAsync(bodyStream);
        var responseBody = Encoding.UTF8.GetString(bodyStream.ToArray());

        // Assert
        Assert.AreEqual("Received request for path: /api/users/123", responseBody);

        client.Close();
        await serverTask;
    }

    [TestMethod]
    public async Task TestRelativeProxy_EmptyPath()
    {
        // Arrange
        var responseGenerator = new Func<HttpProxyRequest, byte[]>(req => Encoding.UTF8.GetBytes($"Received request for path: {req.Path}"));
        var serverTask = StartMockServer(_listener!, responseGenerator, _serverCancellationTokenSource!.Token);

        // Act
        var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)_relativeProxy!.LocalEndPoint).Port);
        var clientStream = client.GetStream();

        var request = "GET / HTTP/1.1\r\nHost: localhost\r\n\r\n";
        await clientStream.WriteAsync(Encoding.UTF8.GetBytes(request));
        await clientStream.FlushAsync();

        using var httpStream = new HttpProxyStream(clientStream);
        var response = await httpStream.ReadResponseHeadersAsync();
        var bodyStream = new MemoryStream();
        await httpStream.TransferUntilEndOfStreamAsync(bodyStream);
        var responseBody = Encoding.UTF8.GetString(bodyStream.ToArray());

        // Assert
        Assert.AreEqual("Received request for path: /api/", responseBody);

        client.Close();
        await serverTask;
    }

    [TestMethod]
    public async Task TestRelativeProxy_QueryParameters()
    {
        // Arrange
        var responseGenerator = new Func<HttpProxyRequest, byte[]>(req => Encoding.UTF8.GetBytes($"Received request for path: {req.Path}"));
        var serverTask = StartMockServer(_listener!, responseGenerator, _serverCancellationTokenSource!.Token);

        // Act
        var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)_relativeProxy!.LocalEndPoint).Port);
        var clientStream = client.GetStream();

        var request = "GET /search?q=test&sort=asc HTTP/1.1\r\nHost: localhost\r\n\r\n";
        await clientStream.WriteAsync(Encoding.UTF8.GetBytes(request));
        await clientStream.FlushAsync();

        using var httpStream = new HttpProxyStream(clientStream);
        var response = await httpStream.ReadResponseHeadersAsync();
        var bodyStream = new MemoryStream();
        await httpStream.TransferUntilEndOfStreamAsync(bodyStream);
        var responseBody = Encoding.UTF8.GetString(bodyStream.ToArray());

        // Assert
        Assert.AreEqual("Received request for path: /api/search?q=test&sort=asc", responseBody);

        client.Close();
        await serverTask;
    }

    [TestMethod]
    public async Task TestRelativeProxy_UnsupportedMethod()
    {
        // Arrange
        var responseGenerator = new Func<HttpProxyRequest, byte[]>(req => Encoding.UTF8.GetBytes($"Received request for path: {req.Path}"));
        var serverTask = StartMockServer(_listener!, responseGenerator, _serverCancellationTokenSource!.Token);

        // Act
        var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)_relativeProxy!.LocalEndPoint).Port);
        var clientStream = client.GetStream();

        var request = "PATCH /users/123 HTTP/1.1\r\nHost: localhost\r\n\r\n";
        await clientStream.WriteAsync(Encoding.UTF8.GetBytes(request));
        await clientStream.FlushAsync();

        using var httpStream = new HttpProxyStream(clientStream);
        var response = await httpStream.ReadResponseHeadersAsync();

        // Assert
        Assert.AreEqual(405, response.StatusCode);

        client.Close();
        await serverTask;
    }

    [TestMethod]
    public async Task TestRelativeProxy_ConcurrentRequests()
    {
        // Arrange
        var responseGenerator = new Func<HttpProxyRequest, byte[]>(req => Encoding.UTF8.GetBytes($"Received request for path: {req.Path}"));
        var serverTask = StartMockServer(_listener!, responseGenerator, _serverCancellationTokenSource!.Token);

        // Act
        var tasks = new Task[10];
        for (int i = 0; i < 10; i++)
        {
            tasks[i] = Task.Run(async () =>
            {
                var client = new TcpClient();
                await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)_relativeProxy!.LocalEndPoint).Port);
                var clientStream = client.GetStream();

                var request = $"GET /users/{i} HTTP/1.1\r\nHost: localhost\r\n\r\n";
                await clientStream.WriteAsync(Encoding.UTF8.GetBytes(request));
                await clientStream.FlushAsync();

                using var httpStream = new HttpProxyStream(clientStream);
                var response = await httpStream.ReadResponseHeadersAsync();
                var bodyStream = new MemoryStream();
                await httpStream.TransferUntilEndOfStreamAsync(bodyStream);
                var responseBody = Encoding.UTF8.GetString(bodyStream.ToArray());

                Assert.AreEqual($"Received request for path: /api/users/{i}", responseBody);

                client.Close();
            });
        }

        await Task.WhenAll(tasks);
        await serverTask;
    }

    private static async Task StartMockServer(TcpListener listener, Func<HttpProxyRequest, byte[]> responseGenerator, CancellationToken cancellationToken)
    {
        try
        {
            var client = await listener.AcceptTcpClientAsync();
            using (var networkStream = client.GetStream())
            {
                using var httpStream = new HttpProxyStream(networkStream);
                while (!cancellationToken.IsCancellationRequested)
                {
                    try
                    {
                        var request = await httpStream.ReadRequestHeadersAsync(cancellationToken);
                        if (request == null)
                            break;
                        var responseBody = responseGenerator(request);
                        var response = new HttpProxyResponse
                        {
                            Version = "HTTP/1.1",
                            StatusCode = 200,
                            Headers = new Dictionary<string, string>
                            {
                                { "Content-Type", "text/plain" },
                                { "Content-Length", responseBody.Length.ToString() }
                            }
                        };
                        await httpStream.WriteResponseAsync(response, cancellationToken);
                        await httpStream.WriteAsync(responseBody, cancellationToken);
                        await httpStream.FlushAsync(cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception)
                    {
                        break;
                    }
                }
            }
            client.Close();
        }
        catch (OperationCanceledException)
        {
            // Server is shutting down
        }
        finally
        {
            listener.Stop();
        }
    }
}