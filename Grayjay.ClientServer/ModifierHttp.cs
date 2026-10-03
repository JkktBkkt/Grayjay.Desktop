using System.IO.Compression;
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
            public HttpHeaders? Headers { get; init; }
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

        /// <summary>
        /// Fetches url through the modifier. With decodeContent, a managed body still carrying a Content-Encoding (such as br) is decoded.
        /// Libcurl bodies are already decoded by libcurl, so they are returned as they are.
        /// </summary>
        public static BytesResult GetBytes(
            ManagedHttpClient client,
            string url,
            IRequestModifier? modifier = null,
            HttpHeaders? headers = null,
            bool decodeContent = false)
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
                if (res.EffectiveUrl != null)
                    finalUrl = res.EffectiveUrl;

                var code = Convert.ToInt32(res.Status);
                return new BytesResult(finalUrl, code, res.BodyBytes ?? Array.Empty<byte>());
            }

            var resp = client.GET(finalUrl, finalHeaders);
            if (resp.Url != null)
                finalUrl = resp.Url;

            if (resp.Body == null)
                return new BytesResult(finalUrl, resp.Code, Array.Empty<byte>());

            var body = resp.Body.AsBytes();
            if (decodeContent && resp.Headers != null)
                body = DecodeContent(resp.Headers, body);
            return new BytesResult(finalUrl, resp.Code, body);
        }

        /// <summary>
        /// Removes every Content-Encoding coding from body, across repeated headers, in reverse order of application.
        /// </summary>
        public static byte[] DecodeContent(HttpHeaders headers, byte[] body)
        {
            var codings = headers.GetAll("content-encoding")
                .SelectMany(value => value.Split(','))
                .Select(part => part.Trim().ToLowerInvariant())
                .ToList();
            if (body.Length == 0 || codings.Count == 0)
                return body;

            codings.Reverse();
            foreach (var coding in codings)
            {
                switch (coding)
                {
                    case "identity":
                    case "":
                        break;
                    case "gzip":
                    case "x-gzip":
                        body = Decompress(body, input => new GZipStream(input, CompressionMode.Decompress));
                        break;
                    case "deflate":
                        // Servers send both zlib-wrapped (RFC 1950) and raw deflate.
                        body = HasZlibHeader(body)
                            ? Decompress(body, input => new ZLibStream(input, CompressionMode.Decompress))
                            : Decompress(body, input => new DeflateStream(input, CompressionMode.Decompress));
                        break;
                    case "br":
                        body = Decompress(body, input => new BrotliStream(input, CompressionMode.Decompress));
                        break;
                    case "zstd":
                        // The streaming decoder also accepts frames that omit the content size.
                        body = Decompress(body, input => new ZstdNet.DecompressionStream(input));
                        break;
                    default:
                        throw new NotSupportedException($"Unsupported content encoding: {coding}");
                }
            }
            return body;
        }

        /// <summary>
        /// RFC 1950 header: compression method 8, window size up to 32K, and a header checksum that is a multiple of 31.
        /// </summary>
        private static bool HasZlibHeader(byte[] body)
        {
            if (body.Length < 2)
                return false;
            var compressionMethodAndFlags = body[0];
            var flags = body[1];
            return (compressionMethodAndFlags & 0x0F) == 8
                && (compressionMethodAndFlags >> 4) <= 7
                && ((compressionMethodAndFlags << 8) | flags) % 31 == 0;
        }

        private static byte[] Decompress(byte[] data, Func<Stream, Stream> createDecoder)
        {
            using var input = new MemoryStream(data);
            using var decoder = createDecoder(input);
            using var output = new MemoryStream();
            decoder.CopyTo(output);
            return output.ToArray();
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
                return new StreamResult(finalUrl, code, bytes.LongLength, new MemoryStream(bytes, writable: false)) { Headers = new HttpHeaders(res.Headers) };
            }

            var resp = client.GET(finalUrl, finalHeaders);
            if (resp.Body == null)
                return new StreamResult(finalUrl, resp.Code, resp.ContentLength, Stream.Null);

            return new StreamResult(finalUrl, resp.Code, resp.ContentLength, resp.Body.AsStream()) { Headers = resp.Headers };
        }
    }
}
