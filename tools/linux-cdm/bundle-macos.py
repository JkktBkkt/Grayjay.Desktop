#!/usr/bin/env python3
import argparse
from pathlib import Path
import shutil

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--publish', type=Path, required=True)
parser.add_argument('--app', type=Path, required=True)
args = parser.parse_args()
source = args.publish / 'playback'
runtime = source / 'runtime'
qemu = runtime / 'qemu-system-x86_64'
if not qemu.is_file() or not (source / 'guest/bundle.json').is_file():
    parser.error('Published playback dependencies are missing')

if any((runtime / 'lib').glob('*.dylib')):
    parser.error('Mac playback packaging requires the static-dependency QEMU build')
contents = args.app / 'Contents'
helper = contents / 'Helpers/qemu-system-x86_64'
helper.parent.mkdir(parents=True, exist_ok=True)
shutil.copy2(qemu, helper)
helper.chmod(0o755)
data = contents / 'Resources/playback'
data.mkdir(parents=True, exist_ok=True)
for item in runtime.iterdir():
    if item.name == qemu.name:
        continue
    if item.is_dir():
        shutil.copytree(item, data / item.name, dirs_exist_ok=True)
    else:
        shutil.copy2(item, data / item.name)
shutil.copytree(source / 'guest', data / 'guest', dirs_exist_ok=True)
print('Bundled playback helper:', helper)
