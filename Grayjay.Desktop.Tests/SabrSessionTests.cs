using System.Net;
using Google.Protobuf;
using Grayjay.ClientServer.Sabr;
using Grayjay.ClientServer.Sabr.Proto;
using Grayjay.Engine.Models.Video.Sources;

namespace Grayjay.Desktop.Tests;

[TestClass]
public class SabrSessionTests
{
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(Uri Uri, HttpMethod Method)> Requests { get; } = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.RequestUri!, request.Method));
            var response = respond(request);
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }

    private static SabrSession CreateSession(HttpClient client) => new(client, "https://initial.test/videoplayback",
        Array.Empty<byte>(), "test", new ClientInfo { ClientName = 1 }, null, false, 10_000_000);

    private static async Task AwaitFailure(SabrSession session)
    {
        session.SetDemand(SabrSession.ROLE_AUDIO, new UMPFormat { Itag = 251, MimeType = "audio/webm", Codecs = "opus" }, 0);
        session.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (session.FatalError == null)
            await Task.Delay(10, timeout.Token);
    }

    [DataTestMethod]
    [DataRow(401)]
    [DataRow(403)]
    public async Task RejectedCredentialsFailWithoutRepeatingTheSameRequest(int status)
    {
        using var handler = new Handler(_ => new HttpResponseMessage((HttpStatusCode)status));
        using var client = new HttpClient(handler);
        using var session = CreateSession(client);
        await AwaitFailure(session);
        Assert.IsInstanceOfType(session.FatalError, typeof(SabrBlockedException));
        Assert.AreEqual(1, handler.Requests.Count);
    }

    [TestMethod]
    public async Task UmpRedirectChangesHostAndPreservesPost()
    {
        var payload = new SabrRedirect { Url = "https://redirected.test/videoplayback" }.ToByteArray();
        Assert.IsTrue(payload.Length < 128);
        var body = new byte[] { UmpPartType.SABR_REDIRECT, (byte)payload.Length }.Concat(payload).ToArray();
        var count = 0;
        using var handler = new Handler(_ => ++count == 1
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) }
            : new HttpResponseMessage(HttpStatusCode.Unauthorized));
        using var client = new HttpClient(handler);
        using var session = CreateSession(client);
        await AwaitFailure(session);
        Assert.AreEqual(2, handler.Requests.Count);
        Assert.AreEqual("redirected.test", handler.Requests[1].Uri.Host);
        Assert.AreEqual(HttpMethod.Post, handler.Requests[1].Method);
    }
}
