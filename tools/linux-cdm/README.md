# Protected playback on Windows and macOS

Windows and macOS use a genuine diskless Linux x64 guest running Google's stock
Widevine L3 CDM. The desktop player routes Widevine HLS and DASH through this
helper automatically. Linux retains its existing native CEF/EME playback.

The app bundles the bridge, minimal QEMU runtime, Linux kernel/initramfs and
helper's FFmpeg demuxer. These dependencies live in
`Grayjay.ClientServer/deps/playback` under the repository's existing Git LFS rule.
Windows/macOS publish output contains only its own host runtime plus the shared
Linux x64 guest. Linux publishes include neither.

Only Google's stock Linux Widevine CDM downloads on first use. The prompt names
Widevine, shows byte progress, supports cancellation and stores verified files in
`Directories.Base/playback-components`. Cached playback does not ask again.
There is no runtime catalog fetch, QEMU download or executable extraction at
playback time. The old hosted `components.json` and runtime caches are unused.
Google's component service is checked at most once a day; a verified cached CDM
remains usable if that check is unavailable. This does not override a service's
license policy or make an already revoked component usable.

## Data path

1. The ordinary source descriptor supplies the manifest and license endpoint.
2. The HLS loader or DASH response interceptor reads public Widevine PSSH data.
3. JustCef bridge RPC starts one guest for the playback session. The stock CDM
   generates a license challenge inside Linux. The host sends that challenge to
   the source's existing license endpoint and returns the response to the CDM.
4. Original MP4 initialization and complete media fragments enter the guest.
   A minimal FFmpeg MOV demuxer reads their CENC/CBCS sample metadata. The stock
   CDM decrypts samples; key bytes never leave the CDM.
5. Clear samples replace encrypted samples at their original MP4 offsets.
   The player receives complete fragments with encryption metadata disabled,
   appends them through its existing MSE player and uses the host's codecs.

The guest has no NIC, disk, SSH server, browser, media decoder or key store.
Its `/tmp` is RAM. It exits on `QUIT` or after 30 seconds without a heartbeat.
Windows uses a local duplex named pipe because QEMU's Windows stdin transport
handshakes once per byte. macOS uses redirected stdin/stdout. Source changes,
window reload/close and application shutdown dispose the guest.

HLS supports fragmented MP4 audio/video with separate rendition initialization
segments. DASH uses dash.js's public response interceptor and supports MP4
SegmentBase, SegmentTemplate and SegmentList requests, seeking and representation
changes. SegmentBase discovery probes and plain subtitles remain untouched.
WebM alternatives are removed from protected manifests; a WebM-only encrypted
source is outside this helper's current format support. MP4 `seig` sample-group
key overrides are explicitly rejected because the MOV demuxer does not implement
them. DASH low latency chunk
appends are disabled: decryption requires a complete fragment.

AVC MP4 NAL lengths such as `0x000001xx` can be mistaken by the CDM for Annex B
start codes. The guest adds a standard **clear** access-unit delimiter to its
CDM input, updates the clear subsample count and strips that delimiter from the
output. Ciphertext, IVs, key IDs and returned MP4 sample offsets are preserved.

## Runtime footprint and platforms

The guest uses one vCPU and 256 MiB RAM. Its kernel and initramfs ZIP is about
15.2 MiB. Minimal QEMU runtime ZIPs are about 3.9 MiB for Mac ARM, 4.2 MiB for Mac
Intel and 5.1 MiB for Windows x64. Widevine is downloaded directly from Google,
currently around 21 MiB compressed; it is not part of these archives.

These Mac runtime builds target macOS 15+. The Windows runtime targets Windows
10+ x64. Windows 11 on ARM uses the same x64 executable through Windows' x64
emulation. The Linux guest is x64 everywhere. Intel Mac prefers HVF; Windows
x64 prefers WHPX; both fall back to TCG when unavailable. ARM hosts use TCG.
The measured Mac ARM QEMU process was about 278 MiB RSS.

## Build and bundle the components

Use Python 3.11.8+ (tar extraction filters), Docker for the guest/Windows build,
and Xcode command-line tools, Meson, Ninja and pkg-config for Mac builds.
`runtime/sources.json` pins upstream source hashes; Windows dependencies have
an additional locked package list. No CDM is involved in release-image builds.

```sh
docker build -t grayjay-cdm-guest:1 tools/linux-cdm/guest
docker run --rm \
  -v "$PWD/tools/linux-cdm/guest:/source:ro" \
  -v /tmp/grayjay-cdm-guest-out:/out grayjay-cdm-guest:1

bash tools/linux-cdm/runtime/build-macos.sh arm64
python3 tools/linux-cdm/package-macos.py \
  --qemu /tmp/grayjay-qemu-mac-arm64/qemu-system-x86_64 \
  --data /tmp/grayjay-qemu-mac-arm64/source/qemu/pc-bios \
  --licenses /tmp/grayjay-qemu-mac-arm64/licenses \
  --identity 'Developer ID Application: YOUR PUBLISHER' \
  --output /tmp/grayjay-playback-release/runtime-osx-arm64.zip

python3 tools/linux-cdm/runtime/fetch-sources.py \
  --cache /tmp/grayjay-sources --windows-deps /tmp/grayjay-win-deps
mkdir -p /tmp/grayjay-win-source /tmp/grayjay-win-out
tar -xf /tmp/grayjay-sources/qemu-11.1.2.tar.xz \
  --strip-components=1 -C /tmp/grayjay-win-source
docker build -t grayjay-qemu-windows:1 \
  -f tools/linux-cdm/runtime/Dockerfile.windows tools/linux-cdm/runtime
docker run --rm \
  -v /tmp/grayjay-win-source:/source \
  -v /tmp/grayjay-win-deps:/deps:ro -v /tmp/grayjay-win-out:/out \
  -v "$PWD/tools/linux-cdm/runtime/build-windows.sh:/build-qemu.sh:ro" \
  grayjay-qemu-windows:1
cp tools/linux-cdm/runtime/sources.json /tmp/grayjay-win-out/licenses/
python3 tools/linux-cdm/package-windows.py \
  --repository mingw64 --cache /tmp/grayjay-win-package-cache \
  --qemu /tmp/grayjay-win-out/qemu-system-x86_64.exe \
  --licenses /tmp/grayjay-win-out/licenses \
  --output /tmp/grayjay-playback-release/runtime-win-x64.zip
```

The guest build records Debian package/source versions and includes library
license receipts. Runtime packages include QEMU/dependency licenses and source
provenance. Publish matching corresponding source archives/build recipes with
the release. Mac release binaries need Developer ID signing and notarization;
the packaging script's default ad hoc signature is for local validation.

Stage the four locally built archives into the tracked dependency tree:

```sh
python3 tools/linux-cdm/stage-bundled.py --directory /tmp/grayjay-playback-release
git add Grayjay.ClientServer/deps/playback
```

Fetch Git LFS files before building. `dotnet publish -r win-x64`/`win-arm64`
includes `playback/runtime` and `playback/guest` next to the executable. Windows
ARM64 uses the x64 runtime. Mac development publish output has the same layout.
Missing dependencies fail the build instead of causing a runtime download.

Both Mac bundle scripts invoke `bundle-macos.py` before signing:

- `Contents/Helpers/qemu-system-x86_64`: native executable.
- `Contents/Resources/playback/data`: QEMU firmware.
- `Contents/Resources/playback/guest`: kernel, base initramfs and integrity metadata.
- `Contents/Resources/playback/licenses`: QEMU/dependency license receipts.

The startup logger writes its initial log in the system temp directory, then
switches to the user data directory; it never creates a log inside the bundle.

The existing signing script signs QEMU with `Entitlements/qemu.entitlements`
(hypervisor/JIT), then signs the outer app and notarizes/staples the complete app.
The Mac runtime has static non-system dependencies. Guest/CDM overlay boot files
are generated only in the user cache; the signed application bundle is read-only.
Kernel/initramfs hashes are checked before use. Executable bytes are not pinned
by our guest receipt, so Developer ID signing can change them normally.

Google's CRX3 package signature is independently checked against Chromium's pinned Widevine
publisher key, in addition to the component service's size/SHA256 metadata.

## Validation and outstanding release work

On macmini, the bundled helper passed clean first-use Widevine download and cached playback,
license HTTP 200, audio/video decode, pause, volume/mute, playback speed, seeking
and the ended event for:

- SoundCloud's actual protected HLS audio source (213.62 seconds).
- Shaka's public Widevine DASH MP4 video (60 seconds).
- Shaka's public Widevine HLS MP4 video (60 seconds).

The Windows cross-publish succeeds. The actual minimal Windows QEMU executable,
with the named-pipe transport under Wine, boots the guest, obtains a public
Widevine license and decrypts all 1,290 encrypted video samples. **Native Windows
CEF/player playback, WHPX on Windows and HVF on Intel Mac still need hardware
validation.** The Intel Mac executable also passed guest license/decryption
validation under Rosetta on macmini. A Wine protocol pass is not a Windows player
pass.

Local ad hoc hardened-runtime signing validates QEMU and the complete Mac app
layout. The ad hoc CEF fixture needs a library-validation exemption because ad
hoc signatures have no Team ID; this exemption is confined to the test signing
copy and is not added to the production CEF entitlements. Production signs all
nested code with the publisher identity.

Mac Developer ID signing found the publisher certificate but failed with
`errSecInternalComponent` over SSH; the keychain/private-key access needs to be
resolved before signed/notarized release artifacts can be produced.

```sh
DOTNET_ROLL_FORWARD=Major dotnet run \
  --project tools/linux-cdm/tests/DownloadTests.csproj -- /path/to/google.crx
node --test tools/linux-cdm/tests/mp4.test.mjs tools/linux-cdm/tests/dash.test.mjs
```

The Mac player script uses the test application's CDP endpoint, the helper
`/tmp/grayjay-cdp.py`, and the installed SoundCloud plugin. Its public video test
modes replace only the test's source-selection response, not the plugin or player
implementation. Run each mode with `--to-end`:

```sh
python3 tools/linux-cdm/tests/mac-player.py --mode dash --to-end --output result.json
```
