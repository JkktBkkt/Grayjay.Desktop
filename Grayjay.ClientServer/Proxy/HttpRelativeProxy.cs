using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Grayjay.Desktop.POC;

namespace Grayjay.ClientServer.Proxy;

public class HttpRelativeProxy : IDisposable
{
    private readonly TcpListener _listener;
    private readonly List<HttpRelativeProxySession> _sessions = new List<HttpRelativeProxySession>();
    private CancellationTokenSource? _cancellationTokenSource = null;
    private readonly HttpProxyRegistryEntry _registryEntry;

    public IPEndPoint LocalEndPoint => (IPEndPoint)_listener.LocalEndpoint;

    public HttpRelativeProxy(IPEndPoint localEndPoint, string baseUrl, RequestHeaderOptions? requestOptions = null, ResponseHeaderOptions? responseOptions = null, Func<HttpProxyResponse, Func<byte[], byte[]>?>? responseModifier = null, string[]? supportedMethods = null, bool followRedirects = true)
    {
        _listener = new TcpListener(localEndPoint.Address, localEndPoint.Port);
        _registryEntry = new HttpProxyRegistryEntry
        {
            Url = baseUrl,
            RequestHeaderOptions = requestOptions ?? new RequestHeaderOptions(),
            ResponseHeaderOptions = responseOptions ?? new ResponseHeaderOptions(),
            ResponseModifier = responseModifier,
            SupportedMethods = supportedMethods,
            FollowRedirects = followRedirects,
            IsRelativeProxy = true,
            SupportRelativeProxy = true
        };
    }

    public void Start()
    {
        _cancellationTokenSource = new CancellationTokenSource();
        Logger.i(nameof(HttpRelativeProxy), $"Started relative proxy listener on {LocalEndPoint}.");
        _listener.Start();

        _ = Task.Run(async () =>
        {
            while (!_cancellationTokenSource.Token.IsCancellationRequested)
            {
                try
                {
                    var client = await _listener.AcceptTcpClientAsync(_cancellationTokenSource.Token);
                    var session = new HttpRelativeProxySession(client.GetStream(), _cancellationTokenSource.Token, (s) => 
                    {
                        lock (_sessions)
                            _sessions.Remove((s as HttpRelativeProxySession)!);
                    }, _registryEntry);
                    session.Start();

                    lock (_sessions)
                        _sessions.Add(session);

                    Logger.i(nameof(HttpRelativeProxy), "Client accepted.");
                }
                catch (Exception e)
                {
                    Logger.e(nameof(HttpRelativeProxy), "Failed to accept client.", e);
                }
            }
        }, _cancellationTokenSource.Token);
    }

    public void Dispose()
    {
        _listener.Stop();
        _cancellationTokenSource?.Cancel();
        lock (_sessions)
            _sessions.Clear();
    }
}