using System.Reflection;
using Grayjay.ClientServer.Models.Downloads;
using Grayjay.Engine.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Grayjay.Desktop.Tests;

[TestClass]
public class DownloadRecoveryTests
{
    [DataTestMethod]
    [DataRow("resume")]
    [DataRow("ignore-range")]
    [DataRow("invalid-range")]
    [DataRow("always-interrupt")]
    [DataRow("cancel")]
    [DataRow("unknown-length")]
    [DataRow("reset-before-headers")]
    public async Task SequentialDownloadRecoversWithoutCorruptingOutput(string behavior)
    {
        var bytes = Enumerable.Range(0, 16000).Select(i => (byte)(i * 31)).ToArray();
        var requests = new List<string>();
        using var cancellation = new CancellationTokenSource();
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        app.MapGet("/source", async context =>
        {
            var range = context.Request.Headers.Range.ToString();
            requests.Add(range);
            if (behavior == "reset-before-headers" && requests.Count == 1)
            {
                context.Abort();
                return;
            }
            int start = range.Length > 0 ? int.Parse(range[6..^1]) : 0;
            if (behavior == "ignore-range") start = 0;
            if (range.Length > 0 && behavior != "ignore-range")
            {
                context.Response.StatusCode = 206;
                var reportedStart = behavior == "invalid-range" ? start + 1 : start;
                context.Response.Headers.ContentRange = $"bytes {reportedStart}-{bytes.Length - 1}/{bytes.Length}";
            }
            if (behavior != "unknown-length")
                context.Response.ContentLength = bytes.Length - start;
            bool interrupt = behavior != "unknown-length" && (requests.Count == 1 || behavior == "always-interrupt");
            int count = interrupt ? Math.Min(2000, bytes.Length - start) : bytes.Length - start;
            await context.Response.Body.WriteAsync(bytes.AsMemory(start, count));
            await context.Response.Body.FlushAsync();
            if (interrupt)
            {
                await Task.Delay(50);
                if (behavior == "cancel") cancellation.Cancel();
                context.Abort();
            }
        });
        await app.StartAsync();
        try
        {
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var output = new MemoryStream();
            output.Write(new byte[] { 9, 8, 7 });
            var method = typeof(VideoDownload).GetMethod("DownloadSourceSequential", BindingFlags.NonPublic | BindingFlags.Instance)!;
            long Download() => (long)method.Invoke(new VideoDownload(), new object?[]
            {
                new ManagedHttpClient(), null, output, address + "/source", null, cancellation.Token
            })!;
            if (behavior is "invalid-range" or "always-interrupt" or "cancel")
            {
                var error = Assert.ThrowsException<TargetInvocationException>(() => Download()).InnerException;
                if (behavior == "invalid-range") Assert.IsInstanceOfType(error, typeof(InvalidDataException));
                if (behavior == "always-interrupt") Assert.IsInstanceOfType(error, typeof(HttpRequestException));
                if (behavior == "cancel") Assert.IsInstanceOfType(error, typeof(OperationCanceledException));
                Assert.AreEqual(behavior == "always-interrupt" ? 6 : behavior == "invalid-range" ? 2 : 1, requests.Count);
            }
            else
            {
                Assert.AreEqual((long)bytes.Length, Download());
                CollectionAssert.AreEqual(new byte[] { 9, 8, 7 }.Concat(bytes).ToArray(), output.ToArray());
                Assert.AreEqual(behavior == "unknown-length" ? 1 : 2, requests.Count);
                if (requests.Count > 1)
                    Assert.AreEqual(behavior == "reset-before-headers" ? "" : "bytes=2000-", requests[1]);
            }
        }
        finally { await app.StopAsync(); }
    }
}
