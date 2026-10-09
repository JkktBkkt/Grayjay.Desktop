#!/usr/bin/env python3
import argparse
import hashlib
import json
from pathlib import Path
import shutil
import tempfile
import zipfile

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--directory', type=Path, required=True, help='Directory containing locally built component ZIPs')
parser.add_argument('--output', type=Path, default=Path(__file__).resolve().parents[2] / 'Grayjay.ClientServer/deps/playback')
args = parser.parse_args()
with tempfile.TemporaryDirectory(prefix='grayjay-playback-stage-') as temporary:
    staged = Path(temporary)
    for archive, directory in [('guest-linux-x64.zip', 'guest'), ('runtime-osx-arm64.zip', 'runtimes/osx-arm64'),
                               ('runtime-osx-x64.zip', 'runtimes/osx-x64'), ('runtime-win-x64.zip', 'runtimes/win-x64')]:
        target = staged / directory
        with zipfile.ZipFile(args.directory / archive) as zip:
            expanded = 0
            for entry in zip.infolist():
                name = entry.filename.replace('\\', '/')
                expanded += entry.file_size
                if name.startswith('/') or ':' in name or any(part in ('.', '..') for part in name.split('/')) or \
                        ((entry.external_attr >> 16) & 0o170000) == 0o120000 or expanded > 512 * 1024 * 1024:
                    raise ValueError('Invalid archive entry: ' + name)
                zip.extract(entry, target)
                path = target / entry.filename
                mode = (entry.external_attr >> 16) & 0o777
                if path.is_file() and mode:
                    path.chmod(mode)
    guest = staged / 'guest'
    if json.loads((guest / 'image.json').read_text())['protocol'] != 3:
        raise ValueError('Guest protocol is incompatible')
    metadata = {'version': '1', 'protocol': 3,
                'kernelSha256': hashlib.sha256((guest / 'vmlinuz').read_bytes()).hexdigest(),
                'initramfsSha256': hashlib.sha256((guest / 'initramfs.cpio.gz').read_bytes()).hexdigest()}
    (guest / 'bundle.json').write_text(json.dumps(metadata, indent=2) + '\n')
    for rid in ('osx-arm64', 'osx-x64', 'win-x64'):
        runtime = staged / 'runtimes' / rid
        executable = runtime / ('qemu-system-x86_64.exe' if rid.startswith('win-') else 'qemu-system-x86_64')
        if not executable.is_file() or not (runtime / 'data/bios-256k.bin').is_file() or not (runtime / 'runtime.json').is_file():
            raise ValueError('Incomplete runtime: ' + rid)
        if rid.startswith('osx-'):
            executable.chmod(0o755)
    args.output.mkdir(parents=True, exist_ok=True)
    shutil.copytree(staged, args.output, dirs_exist_ok=True)
print('Staged playback dependencies:', args.output)
