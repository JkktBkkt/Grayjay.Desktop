namespace Grayjay.ClientServer.Proxy;

public class HttpRelativeProxySession : HttpBaseProxySession
{
    private readonly HttpProxyRegistryEntry _registryEntry;

    public HttpRelativeProxySession(Stream stream, CancellationToken cancellationToken, Action<HttpBaseProxySession> onDisconnected, HttpProxyRegistryEntry registryEntry)
        : base(stream, cancellationToken, onDisconnected)
    {
        _registryEntry = registryEntry ?? throw new ArgumentNullException(nameof(registryEntry));
    }

    protected override (HttpProxyRegistryEntry?, bool) GetEntry(HttpProxyRequest? incomingRequest)
    {
        if (incomingRequest == null)
            return (null, false);
        return (_registryEntry, true);
    }
}