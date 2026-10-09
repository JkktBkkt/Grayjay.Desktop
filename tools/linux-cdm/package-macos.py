#!/usr/bin/env python3
import argparse
import hashlib
import json
from pathlib import Path
import shutil
import subprocess
import tempfile
import zipfile

def run(*args):
    return subprocess.check_output(args, text=True).strip()

def dependencies(path):
    lines = run('otool', '-L', str(path)).splitlines()[1:]
    return [line.strip().split(' (')[0] for line in lines]

def system(path):
    return path.startswith(('/usr/lib/', '/System/Library/'))

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--qemu', type=Path, required=True)
    parser.add_argument('--data', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--identity', default='-')
    parser.add_argument('--licenses', type=Path, help='Source licenses and build receipts for a custom QEMU')
    args = parser.parse_args()
    executable = args.qemu.resolve()
    sources = {str(executable): executable}
    queue = [executable]
    while queue:
        source = queue.pop()
        for dependency in dependencies(source):
            if system(dependency) or dependency in sources:
                continue
            if not dependency.startswith('/'):
                raise RuntimeError('Unresolved dependency: ' + dependency)
            path = Path(dependency).resolve()
            if not path.is_file():
                raise RuntimeError('Missing dependency: ' + dependency)
            sources[dependency] = path
            queue.append(path)
    with tempfile.TemporaryDirectory(prefix='grayjay-qemu-') as temporary:
        root = Path(temporary)
        targets = {}
        names = {}
        for reference, source in sources.items():
            relative = 'qemu-system-x86_64' if source == executable else 'lib/' + source.name
            if relative in names and names[relative] != source:
                raise RuntimeError('Conflicting library names: ' + relative)
            names[relative] = source
            destination = root / relative
            destination.parent.mkdir(parents=True, exist_ok=True)
            if not destination.exists():
                shutil.copy2(source, destination)
                destination.chmod(0o755)
            targets[reference] = destination
        for destination in set(targets.values()):
            if destination.parent.name == 'lib':
                subprocess.check_call(['install_name_tool', '-id', '@loader_path/' + destination.name, str(destination)])
            for dependency in dependencies(destination):
                if system(dependency):
                    continue
                target = targets.get(dependency)
                if target is None:

                    if dependency == '@loader_path/' + destination.name:
                        continue
                    raise RuntimeError('Unknown dependency: ' + dependency)
                prefix = '@loader_path/' if destination.parent.name == 'lib' else '@loader_path/lib/'
                subprocess.check_call(['install_name_tool', '-change', dependency, prefix + target.name, str(destination)])
        data = root / 'data'
        data.mkdir()
        for filename in ('bios-256k.bin', 'kvmvapic.bin', 'linuxboot_dma.bin'):
            shutil.copy2(args.data / filename, data / filename)

        licenses = root / 'licenses'
        licenses.mkdir()
        if args.licenses:
            shutil.copytree(args.licenses, licenses, dirs_exist_ok=True)
        elif 'Cellar' not in executable.parts:
            raise RuntimeError('Custom builds require --licenses with QEMU and dependency license receipts')
        for source in set(sources.values()):
            parts = source.parts
            if 'Cellar' not in parts:
                continue
            index = parts.index('Cellar')
            cellar = Path(*parts[:index + 3])
            for filename in ('COPYING', 'COPYING.LIB', 'LICENSE', 'LICENSE.txt', 'LICENSE.md', '.brew'):
                item = cellar / filename
                if item.is_file():
                    shutil.copy2(item, licenses / (parts[index + 1] + '-' + item.name))
            brew = cellar / '.brew'
            if brew.is_dir():
                for item in brew.glob('*.rb'):
                    shutil.copy2(item, licenses / item.name)
        entitlement = root / 'entitlements.plist'
        entitlement.write_text('<?xml version="1.0"?><!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd"><plist version="1.0"><dict><key>com.apple.security.hypervisor</key><true/><key>com.apple.security.cs.allow-jit</key><true/><key>com.apple.security.cs.allow-unsigned-executable-memory</key><true/></dict></plist>')
        for library in (root / 'lib').glob('*.dylib'):
            subprocess.check_call(['codesign', '--force', '--sign', args.identity, str(library)])
        command = ['codesign', '--force', '--sign', args.identity, '--entitlements', str(entitlement)]
        if args.identity != '-':
            command += ['--options', 'runtime', '--timestamp']
        subprocess.check_call(command + [str(root / 'qemu-system-x86_64')])
        entitlement.unlink()
        metadata = {'qemu': run(str(root / 'qemu-system-x86_64'), '--version').splitlines()[0],
                    'architecture': run('lipo', '-archs', str(root / 'qemu-system-x86_64')), 'signingIdentity': args.identity}
        (root / 'runtime.json').write_text(json.dumps(metadata, indent=2))
        args.output.parent.mkdir(parents=True, exist_ok=True)
        with zipfile.ZipFile(args.output, 'w', zipfile.ZIP_DEFLATED, compresslevel=9) as archive:
            for file in sorted(root.rglob('*')):
                if file.is_file():
                    archive.write(file, file.relative_to(root))
    digest = hashlib.sha256(args.output.read_bytes()).hexdigest()
    print(json.dumps({'file': str(args.output), 'sha256': digest, 'size': args.output.stat().st_size, **metadata}, indent=2))

if __name__ == '__main__':
    main()
