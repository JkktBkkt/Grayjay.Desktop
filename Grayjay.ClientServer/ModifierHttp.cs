using Grayjay.Engine.Models;
using Grayjay.Engine.Web;
using Grayjay.Engine.Models.Video.Additions;
using Grayjay.Engine.Packages;

namespace Grayjay.ClientServer
{
    public static partial class ModifierHttp
    {
        public readonly record struct BytesResult(string FinalUrl, int Code, byte[] Bytes)
        {
            public bool IsOk => Code >= 200 && Code < 300;
        }

        public readonly record struct StreamResult(string FinalUrl, int Code, long ContentLength, Stream Stream)
        {
            public bool IsOk => Code >= 200 && Code < 300;
        }

        public readonly record struct BoundedBytesResult(string FinalUrl, int Code, byte[] Bytes, bool LimitExceeded)
        {
            public bool IsOk => Code >= 200 && Code < 300;
        }

        private static List<KeyValuePair<string, string>> ToHeaderList(HttpHeaders headers)
        {
            var list = new List<KeyValuePair<string, string>>();
            foreach (var h in headers)
            {
                if (!string.IsNullOrEmpty(h.Key) && h.Value != null)
                    list.Add(new KeyValuePair<string, string>(h.Key, h.Value));
            }
            return list;
        }

        public static BytesResult GetBytes(
            ManagedHttpClient client,
            string url,
            IRequestModifier? modifier = null,
            HttpHeaders? headers = null)
            => SendBytes(client, "GET", url, null, modifier, headers);

        public static BytesResult PostBytes(
            ManagedHttpClient client,
            string url,
            byte[] body,
            IRequestModifier? modifier = null,
            HttpHeaders? headers = null)
            => SendBytes(client, "POST", url, body, modifier, headers);

        private static BytesResult SendBytes(
            ManagedHttpClient client,
            string method,
            string url,
            byte[]? body,
            IRequestModifier? modifier,
            HttpHeaders? headers)
        {
            headers ??= new HttpHeaders();
            var modified = modifier?.ModifyRequest(url, headers);

            var finalUrl = modified?.Url ?? url;
            var finalHeaders = modified?.Headers ?? headers;
            var impersonate = modified?.Options?.ImpersonateTarget;

            if (!string.IsNullOrEmpty(impersonate))
            {
                var res = Libcurl.Perform(new Libcurl.Request
                {
                    Url = finalUrl,
                    Method = method,
                    Headers = ToHeaderList(finalHeaders),
                    Body = body,
                    ImpersonateTarget = impersonate
                });
                if (res.EffectiveUrl != null)
                    finalUrl = res.EffectiveUrl;

                var code = Convert.ToInt32(res.Status);
                return new BytesResult(finalUrl, code, res.BodyBytes ?? Array.Empty<byte>());
            }

            var resp = (body != null)
                ? client.Request(method, finalUrl, body, finalHeaders)
                : client.GET(finalUrl, finalHeaders);
            if (resp.Url != null)
                finalUrl = resp.Url;

            if (resp.Body == null)
                return new BytesResult(finalUrl, resp.Code, Array.Empty<byte>());

            return new BytesResult(finalUrl, resp.Code, resp.Body.AsBytes());
        }

        public static BoundedBytesResult GetBytesBounded(
            ManagedHttpClient client,
            string url,
            IRequestModifier? modifier,
            HttpHeaders? headers,
            long maxBytes)
        {
            var res = GetStream(client, url, modifier, headers);
            using var responseStream = res.Stream;

            if (res.ContentLength > maxBytes)
                return new BoundedBytesResult(res.FinalUrl, res.Code, Array.Empty<byte>(), true);

            using var buffered = new MemoryStream();
            var chunk = new byte[64 * 1024];
            long total = 0;
            while (true)
            {
                int read = responseStream.Read(chunk, 0, chunk.Length);
                if (read <= 0)
                    break;

                total += read;
                if (total > maxBytes)
                    return new BoundedBytesResult(res.FinalUrl, res.Code, Array.Empty<byte>(), true);

                buffered.Write(chunk, 0, read);
            }

            return new BoundedBytesResult(res.FinalUrl, res.Code, buffered.ToArray(), false);
        }

        public static StreamResult GetStream(
            ManagedHttpClient client,
            string url,
            IRequestModifier? modifier = null,
            HttpHeaders? headers = null)
        {
            headers ??= new HttpHeaders();
            var modified = modifier?.ModifyRequest(url, headers);

            var finalUrl = modified?.Url ?? url;
            var finalHeaders = modified?.Headers ?? headers;
            var impersonate = modified?.Options?.ImpersonateTarget;

            if (!string.IsNullOrEmpty(impersonate))
            {
                var res = Libcurl.Perform(new Libcurl.Request
                {
                    Url = finalUrl,
                    Method = "GET",
                    Headers = ToHeaderList(finalHeaders),
                    ImpersonateTarget = impersonate
                });

                var bytes = res.BodyBytes ?? Array.Empty<byte>();
                var code = Convert.ToInt32(res.Status);
                return new StreamResult(finalUrl, code, bytes.LongLength, new MemoryStream(bytes, writable: false));
            }

            var resp = client.GET(finalUrl, finalHeaders);
            var contentLength = ReadContentLength(resp.Headers);
            if (resp.Body == null)
                return new StreamResult(finalUrl, resp.Code, contentLength, Stream.Null);

            return new StreamResult(finalUrl, resp.Code, contentLength, resp.Body.AsStream());
        }

        private static long ReadContentLength(HttpHeaders headers)
        {
            if (headers.TryGetFirst("content-length", out var value) && long.TryParse(value, out var contentLength))
                return contentLength;
            return -1;
        }
    }
}
