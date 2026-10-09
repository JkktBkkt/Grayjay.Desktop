#!/bin/bash
set -euo pipefail
export PATH="/opt/homebrew/bin:$PATH"
arch="${1:-x86_64}"
family="$arch"
hvf=--enable-hvf
if [ "$arch" = arm64 ]; then family=aarch64; hvf=--disable-hvf; fi
root="${CDM_BUILD_ROOT:-/tmp/grayjay-qemu-mac-$arch}"
source_cache="${CDM_SOURCE_CACHE:-/tmp}"
script_dir="$(cd "$(dirname "$0")" && pwd)"
python3 "$script_dir/fetch-sources.py" --cache "$source_cache"
mkdir -p "$root/source" "$root/deps/lib/pkgconfig"
if [ ! -d "$root/source/pcre2" ]; then
 mkdir "$root/source/pcre2" "$root/source/glib" "$root/source/qemu"
 tar -xf "$source_cache/pcre2-10.49.tar.bz2" --strip-components=1 -C "$root/source/pcre2"
 tar -xf "$source_cache/glib-2.90.1.tar.xz" --strip-components=1 -C "$root/source/glib"
 tar -xf "$source_cache/qemu-11.1.2.tar.xz" --strip-components=1 -C "$root/source/qemu"
fi
export PKG_CONFIG_LIBDIR="$root/deps/lib/pkgconfig"
export PKG_CONFIG_PATH="$root/deps/lib/pkgconfig"
export MACOSX_DEPLOYMENT_TARGET=15.0
sdk=$(xcrun --show-sdk-path)
cat > "$root/deps/lib/pkgconfig/libffi.pc" <<PC
prefix=$sdk/usr
libdir=\${prefix}/lib
includedir=\${prefix}/include/ffi
Name: libffi
Description: macOS system foreign function interface
Version: 3.4.6
Libs: -L\${libdir} -lffi
Cflags: -I\${includedir}
PC
cat > "$root/deps/lib/pkgconfig/zlib.pc" <<PC
prefix=$sdk/usr
libdir=\${prefix}/lib
includedir=\${prefix}/include
Name: zlib
Description: macOS system zlib
Version: 1.2.12
Libs: -lz
Cflags:
PC
cd "$root/source/pcre2"
./configure CC="clang -arch $arch" --prefix="$root/deps" --disable-shared --enable-static --disable-pcre2grep --disable-pcre2test --disable-jit
make -j4
make install
cat > "$root/cross.ini" <<CROSS
[binaries]
c = ['clang', '-arch', '$arch']
cpp = ['clang++', '-arch', '$arch']
objc = ['clang', '-arch', '$arch']
pkg-config = '/opt/homebrew/bin/pkg-config'
[host_machine]
system = 'darwin'
subsystem = 'macos'
cpu_family = '$family'
cpu = '$arch'
endian = 'little'
[properties]
needs_exe_wrapper = false
CROSS
if [ ! -f "$root/glib-build/build.ninja" ]; then
 meson setup "$root/glib-build" "$root/source/glib" --cross-file "$root/cross.ini" --prefix="$root/deps" --libdir=lib --default-library=static -Dtests=false -Dinstalled_tests=false -Ddocumentation=false -Dman-pages=disabled -Dintrospection=disabled -Dnls=disabled -Dlibmount=disabled -Dselinux=disabled
fi
ninja -C "$root/glib-build" -j4
ninja -C "$root/glib-build" install
mkdir -p "$root/qemu-build"
cd "$root/qemu-build"
AR=/usr/bin/ar NM=/usr/bin/nm RANLIB=/usr/bin/ranlib STRIP=/usr/bin/strip PKG_CONFIG=/opt/homebrew/bin/pkg-config "$root/source/qemu/configure" --without-default-features --target-list=x86_64-softmmu --cpu=$family --cross-prefix=$arch-apple-darwin- --cc="clang -arch $arch" --cxx="clang++ -arch $arch" --objcc="clang -arch $arch" --enable-tcg "$hvf" --disable-docs --disable-tools --disable-user --disable-fdt --disable-werror --audio-drv-list= --extra-ldflags="$(pkg-config --static --libs glib-2.0 gmodule-2.0)"
ninja -j4 qemu-system-x86_64
cp qemu-system-x86_64 "$root/qemu-system-x86_64"
strip -S "$root/qemu-system-x86_64"
mkdir -p "$root/licenses"
cp "$root/source/qemu/COPYING" "$root/licenses/QEMU-COPYING"
cp "$root/source/qemu/COPYING.LIB" "$root/licenses/QEMU-COPYING.LIB"
cp "$root/source/glib/COPYING" "$root/licenses/GLib-COPYING"
cp "$root/source/pcre2/LICENCE.md" "$root/licenses/PCRE2-LICENCE.md"
cp "$script_dir/sources.json" "$root/licenses/sources.json"
