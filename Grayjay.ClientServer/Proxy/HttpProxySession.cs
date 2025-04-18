namespace Grayjay.ClientServer.Proxy;

public class HttpProxySession : HttpBaseProxySession
{
    private readonly HttpProxy _proxy;

    public HttpProxySession(HttpProxy proxy, Stream stream, CancellationToken cancellationToken, Action<HttpBaseProxySession> onDisconnected)
        : base(stream, cancellationToken, onDisconnected)
    {
        _proxy = proxy;
    }

    protected override (HttpProxyRegistryEntry?, bool) GetEntry(HttpProxyRequest? incomingRequest)
    {
        if (incomingRequest == null)
            return (null, false);

        var idString = incomingRequest.Path.StartsWith("/") ? incomingRequest.Path.Substring(1) : incomingRequest.Path;
        if (idString.Contains("?"))
            idString = idString.Substring(0, idString.IndexOf("?"));

        Guid id = Guid.Empty;
        bool isRelativeProxy = false;
        if (!Guid.TryParse(idString, out id))
        {
            string? referer;
            int port = _proxy.LocalEndPoint.Port;
            if (incomingRequest.Headers.TryGetValue("referer", out referer) && referer != null)
            {
                if (referer.Contains("localhost:" + port) || referer.Contains("127.0.0.1:" + port))
                {
                    Uri refererUri = new Uri(referer);
                    idString = refererUri.LocalPath.StartsWith("/") ? refererUri.LocalPath.Substring(1) : refererUri.LocalPath;
                    if (Guid.TryParse(idString, out id))
                        isRelativeProxy = true;
                }
            }
        }

        if (id == Guid.Empty)
            throw new InvalidOperationException($"Request was not a valid proxy request: " + incomingRequest.Path);

        var registryEntry = _proxy.GetEntry(id);
        return (registryEntry, isRelativeProxy);
    }
}