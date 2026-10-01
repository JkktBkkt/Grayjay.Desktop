using System.Security.Cryptography;
using System.Text;

namespace Grayjay.ClientServer;

public sealed class LocalMediaRegistry : IDisposable
{
    private sealed record Artifact(string Path, string ContentType, long Length, DateTime Modified);
    private readonly object _lock = new();
    private readonly Dictionary<string, object> _entries = new();
    private bool _disposed;

    private string Register(object entry, string identity)
    {
        var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        lock (_lock)
        {
            if (_disposed) throw Missing();
            _entries.TryAdd(id, entry);
        }
        return id;
    }

    public string RegisterFile(string path, string contentType)
    {
        var file = new FileInfo(path);
        if (!file.Exists) throw Missing();
        var artifact = new Artifact(file.FullName, contentType, file.Length, file.LastWriteTimeUtc);
        return Register(artifact, $"file\n{artifact.Path}\n{artifact.ContentType}\n{artifact.Length}\n{artifact.Modified.Ticks}");
    }

    public string RegisterManifest(string manifest) => Register(manifest, "manifest\n" + manifest);

    private T Get<T>(string id)
    {
        lock (_lock)
            return _entries.TryGetValue(id, out var entry) && entry is T value ? value : throw Missing();
    }

    public string GetManifest(string id) => Get<string>(id);

    public (FileStream Stream, string ContentType) Open(string id)
    {
        var artifact = Get<Artifact>(id);
        FileStream stream;
        try { stream = new FileStream(artifact.Path, FileMode.Open, FileAccess.Read, FileShare.Read); }
        catch (FileNotFoundException) { throw Missing(); }
        catch (DirectoryNotFoundException) { throw Missing(); }
        try
        {
            if (stream.Length != artifact.Length || File.GetLastWriteTimeUtc(stream.SafeFileHandle) != artifact.Modified)
                throw Missing();
            return (stream, artifact.ContentType);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
            _entries.Clear();
        }
    }

    private static BadHttpRequestException Missing() => new("Local media is no longer available", StatusCodes.Status404NotFound);
}
