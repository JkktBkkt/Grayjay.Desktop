namespace Grayjay.Desktop.CEF.LinuxCdm;

internal sealed record DownloadProgress(string Stage, string Message, long Received = 0, long? Total = null);
internal sealed record DownloadAsset(string Url, string Sha256, long Size);
internal sealed record InstalledCdm(string Version, string Path, string Sha256);
internal sealed record PlayerRuntime(string Directory, string Executable, string Kernel, string Initramfs);
