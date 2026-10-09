using System.Text.Json;

namespace Grayjay.Desktop.CEF.LinuxCdm;

internal sealed record BundledRuntime(string Directory, string Executable, string Kernel, string Initramfs,
    string KernelSha256, string InitramfsSha256)
{
    internal static bool Available(string applicationDirectory, bool windows)
    {
        try { Resolve(applicationDirectory, windows); return true; }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return false; }
    }
    internal static BundledRuntime Resolve(string applicationDirectory, bool windows)
    {
        var app = new DirectoryInfo(Path.GetFullPath(applicationDirectory));
        bool macBundle = !windows && app.Name == "MacOS" && app.Parent?.Name == "Contents";
        string root = macBundle ? Path.Combine(app.Parent!.FullName, "Resources", "playback") : Path.Combine(app.FullName, "playback");
        string runtime = macBundle ? root : Path.Combine(root, "runtime");
        string executable = macBundle ? Path.Combine(app.Parent!.FullName, "Helpers", "qemu-system-x86_64") :
            Path.Combine(runtime, windows ? "qemu-system-x86_64.exe" : "qemu-system-x86_64");
        string guest = Path.Combine(root, "guest");
        string kernel = Path.Combine(guest, "vmlinuz"), initramfs = Path.Combine(guest, "initramfs.cpio.gz");
        if (!File.Exists(executable) || !File.Exists(kernel) || !File.Exists(initramfs) || !File.Exists(Path.Combine(runtime, "data", "bios-256k.bin")))
            throw new FileNotFoundException("Bundled playback components are missing. Reinstall Grayjay to restore them.");
        var metadata = JsonSerializer.Deserialize<Metadata>(File.ReadAllText(Path.Combine(guest, "bundle.json")),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (metadata is null || metadata.Version != RuntimeAssets.Release || metadata.Protocol != RuntimeAssets.ProtocolVersion ||
            !Hash(metadata.KernelSha256) || !Hash(metadata.InitramfsSha256))
            throw new InvalidDataException("Bundled playback components are incompatible with this version of Grayjay.");
        return new(runtime, executable, kernel, initramfs, metadata.KernelSha256, metadata.InitramfsSha256);
    }
    internal async Task VerifyGuestAsync(CancellationToken cancellation)
    {
        if (!await VerifiedDownload.MatchesAsync(Kernel, KernelSha256, cancellation) ||
            !await VerifiedDownload.MatchesAsync(Initramfs, InitramfsSha256, cancellation))
            throw new InvalidDataException("Bundled Linux guest is damaged. Reinstall Grayjay to restore it.");
    }
    private static bool Hash(string? hash) => hash is { Length: 64 } && hash.All(Uri.IsHexDigit);
    private sealed record Metadata(string Version, int Protocol, string KernelSha256, string InitramfsSha256);
}
