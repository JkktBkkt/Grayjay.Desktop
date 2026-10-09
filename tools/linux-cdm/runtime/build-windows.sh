#!/bin/bash
set -euo pipefail
cd /source
mkdir -p /out/build
cd /out/build
/source/configure --without-default-features --target-list=x86_64-softmmu --cross-prefix=x86_64-w64-mingw32- --cpu=x86_64 --enable-tcg --enable-whpx --disable-docs --disable-tools --disable-user --disable-fdt --disable-werror --audio-drv-list= --extra-cflags=-I/deps/mingw64/include --extra-ldflags=-L/deps/mingw64/lib
ninja -j4 qemu-system-x86_64.exe
cp qemu-system-x86_64.exe /out/qemu-system-x86_64.exe
x86_64-w64-mingw32-strip /out/qemu-system-x86_64.exe
mkdir -p /out/data /out/licenses
cp /source/pc-bios/bios-256k.bin /source/pc-bios/kvmvapic.bin /source/pc-bios/linuxboot_dma.bin /out/data/
cp /source/COPYING /out/licenses/QEMU-COPYING
cp /source/COPYING.LIB /out/licenses/QEMU-COPYING.LIB
