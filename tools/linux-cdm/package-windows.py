#!/usr/bin/env python3
import argparse
import hashlib
import io
import json
from pathlib import Path
import re
import shutil
import subprocess
import tarfile
import tempfile
import urllib.request
import zipfile

BASE = 'https://mirror.msys2.org/mingw/'
SYSTEM = set(('advapi32 bcrypt crypt32 dnsapi gdi32 imm32 iphlpapi kernel32 ntdll ole32 oleaut32 psapi secur32 '
              'shell32 shlwapi user32 uuid version winmm ws2_32 wtsapi32 setupapi d3d11 dxgi winhvplatform '
              'dbghelp ucrtbase msvcrt comdlg32 vfw32 avrt normaliz powrprof comctl32 rpcrt4 winspool '
              'pathcch propsys wldap32 winscard msimg32 winhttp wininet usp10 dwrite uxtheme hid gdiplus '
              'dwmapi dcomp dxcore msacm32 wintrust netapi32 userenv oleacc cryptbase ncrypt mswsock').split())

def index(database, repository):
    raw = subprocess.check_output(['zstd', '-d', '-c', str(database)])
    packages, files = {}, {}
    with tarfile.open(fileobj=io.BytesIO(raw)) as archive:
        for member in archive:
            if member.name.endswith('/desc'):
                fields, key = {}, None
                for line in archive.extractfile(member).read().decode().splitlines():
                    if line.startswith('%'):
                        key = line.strip('%'); fields[key] = []
                    elif key and line:
                        fields[key].append(line)
                packages[member.name.split('/')[0]] = fields
            elif member.name.endswith('/files'):
                for name in archive.extractfile(member).read().decode().splitlines():
                    if name.startswith(repository + '/bin/') and name.lower().endswith(('.dll', '.exe')):
                        files[name.split('/')[-1].lower()] = (member.name.split('/')[0], name)
    return packages, files

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--cache', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--repository', choices=['ucrt64', 'mingw64'], default='ucrt64')
    parser.add_argument('--qemu', type=Path, help='Use a minimal QEMU built from source instead of the distribution executable')
    parser.add_argument('--licenses', type=Path, help='Source licenses and build receipts for a custom QEMU')
    args = parser.parse_args()
    args.cache.mkdir(parents=True, exist_ok=True)
    base = BASE + args.repository + '/'
    database = args.cache / (args.repository + '.files')
    if not database.exists():
        urllib.request.urlretrieve(base + args.repository + '.files', database)
    packages, files = index(database, args.repository)
    receipts = {}
    with tempfile.TemporaryDirectory(prefix='grayjay-win-qemu-') as temporary:
        root = Path(temporary)
        (root / 'licenses').mkdir(); (root / 'data').mkdir()
        if args.qemu:
            if not args.licenses:
                raise RuntimeError('Custom builds require --licenses with QEMU license and build receipts')
            shutil.copytree(args.licenses, root / 'licenses', dirs_exist_ok=True)
        def package(name):
            if name in receipts:
                return args.cache / name
            info = packages[name]
            archive = args.cache / info['FILENAME'][0]
            if not archive.exists() or hashlib.sha256(archive.read_bytes()).hexdigest() != info['SHA256SUM'][0]:
                print('Downloading ' + info['FILENAME'][0], flush=True)
                urllib.request.urlretrieve(base + info['FILENAME'][0], archive)
            if hashlib.sha256(archive.read_bytes()).hexdigest() != info['SHA256SUM'][0]:
                raise RuntimeError('MSYS2 package hash verification failed: ' + name)
            destination = args.cache / name
            destination.mkdir(exist_ok=True)
            process = subprocess.Popen(['zstd', '-d', '-c', str(archive)], stdout=subprocess.PIPE)
            with tarfile.open(fileobj=process.stdout, mode='r|') as contents:
                for entry in contents:
                    if not entry.isfile():
                        continue
                    filename = entry.name.split('/')[-1]
                    target = None
                    if entry.name == args.repository + '/bin/qemu-system-x86_64.exe' or (entry.name.startswith(args.repository + '/bin/') and entry.name.endswith('.dll')):
                        target = destination / filename
                    elif '/share/licenses/' in entry.name:
                        license_path = root / 'licenses' / name
                        license_path.mkdir(exist_ok=True)
                        target = license_path / filename
                    elif '/share/qemu/' in entry.name and filename in ('bios-256k.bin', 'kvmvapic.bin', 'linuxboot.bin', 'linuxboot_dma.bin'):
                        target = root / 'data' / filename
                    if target:
                        with target.open('wb') as output:
                            shutil.copyfileobj(contents.extractfile(entry), output)
            if process.wait() != 0:
                raise RuntimeError('Could not unpack MSYS2 package: ' + name)
            receipts[name] = {key: info.get(key) for key in ('NAME', 'VERSION', 'URL', 'FILENAME', 'SHA256SUM', 'LICENSE', 'PACKAGER')}
            return destination
        queue = ['qemu-system-x86_64.exe']; copied = set()
        while queue:
            filename = queue.pop().lower()
            if filename in copied:
                continue
            stem = filename.removesuffix('.dll')
            if stem in SYSTEM or filename.startswith(('api-ms-win-', 'ext-ms-win-')):
                continue
            if filename == 'qemu-system-x86_64.exe' and args.qemu:
                original = args.qemu
            elif filename not in files:
                raise RuntimeError('Unresolved Windows DLL: ' + filename)
            else:
                owner, relative = files[filename]
                original = package(owner) / relative.split('/')[-1]
            destination = root / original.name
            shutil.copy2(original, destination)
            copied.add(filename)
            imports = subprocess.check_output(['llvm-readobj', '--coff-imports', str(destination)], text=True)
            queue.extend(re.findall(r'^\s*Name: (\S+\.dll)$', imports, re.MULTILINE | re.IGNORECASE))
        if args.qemu:

            for name in ('bios-256k.bin', 'kvmvapic.bin', 'linuxboot_dma.bin'):
                firmware = args.qemu.parent / 'data' / name
                if not firmware.is_file():
                    raise RuntimeError('Missing boot firmware: ' + str(firmware))
                shutil.copy2(firmware, root / 'data' / name)
        else:
            common = next(name for name in packages if name.startswith('mingw-w64-ucrt-x86_64-qemu-common-'))
            package(common)
        (root / 'runtime.json').write_text(json.dumps({'architecture': 'x86_64', 'packages': receipts}, indent=2))
        args.output.parent.mkdir(parents=True, exist_ok=True)
        with zipfile.ZipFile(args.output, 'w', zipfile.ZIP_DEFLATED, compresslevel=9) as archive:
            for file in sorted(root.rglob('*')):
                if file.is_file():
                    archive.write(file, file.relative_to(root))
    print(json.dumps({'file': str(args.output), 'size': args.output.stat().st_size,
                      'sha256': hashlib.sha256(args.output.read_bytes()).hexdigest(), 'dllCount': len(copied) - 1}, indent=2))

if __name__ == '__main__':
    main()
