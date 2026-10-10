#!/usr/bin/env python3
"""Check that dependency staging cannot preserve or reintroduce Windows QEMU."""
import hashlib
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import zipfile

stager = Path(__file__).resolve().parents[1] / 'stage-bundled.py'
with tempfile.TemporaryDirectory(prefix='grayjay-staging-test-') as temporary:
    root = Path(temporary)
    archives = root / 'archives'
    archives.mkdir()
    output = root / 'output'
    windows = output / 'runtimes/win-x64'
    windows.mkdir(parents=True)
    (windows / 'qemu-system-x86_64.exe').write_bytes(b'obsolete')

    def archive(name, files):
        with zipfile.ZipFile(archives / name, 'w') as bundle:
            for path, data in files.items():
                bundle.writestr(path, data)

    archive('guest-linux-x64.zip', {
        'vmlinuz': b'kernel', 'initramfs.cpio.gz': b'guest',
        'image.json': json.dumps({'protocol': 3})})
    for rid in ('osx-arm64', 'osx-x64'):
        archive('runtime-' + rid + '.zip', {
            'qemu-system-x86_64': b'mac', 'data/bios-256k.bin': b'bios',
            'runtime.json': '{}'})
    files = {'blink.exe': b'blink', 'cygwin1.dll': b'cygwin'}
    files['blink-runtime.json'] = json.dumps({
        'version': '1', 'protocol': 3,
        'blinkSha256': hashlib.sha256(b'blink').hexdigest(),
        'cygwinSha256': hashlib.sha256(b'cygwin').hexdigest()})
    archive('runtime-win-x64.zip', files)
    command = [sys.executable, str(stager), '--directory', str(archives),
               '--output', str(output)]
    subprocess.run(command, check=True, capture_output=True)
    assert not (windows / 'qemu-system-x86_64.exe').exists()
    assert (windows / 'blink.exe').read_bytes() == b'blink'

    files['qemu-system-x86_64.exe'] = b'forbidden'
    archive('runtime-win-x64.zip', files)
    rejected = subprocess.run(command, capture_output=True)
    assert rejected.returncode != 0
    assert b'Unexpected Windows runtime file' in rejected.stderr
    assert (windows / 'blink.exe').read_bytes() == b'blink'
    assert not (windows / 'qemu-system-x86_64.exe').exists()
print('PASS stale Windows QEMU removal and mixed-archive rejection')
