#!/usr/bin/env python3
"""Build and execute the hardware-free getting-started app using public packages."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import platform
import re
import shutil
import struct
import subprocess
import tomllib

ROOT = Path(__file__).resolve().parents[1]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--framework', choices=['net8.0', 'net10.0'], default='net8.0')
    args = parser.parse_args()
    out = args.output.resolve()
    if out == ROOT or ROOT in out.parents or out in ROOT.parents:
        raise ValueError('Use a separate empty output directory outside the checkout')
    if out.exists() and any(out.iterdir()):
        raise ValueError('Use an empty output directory')
    if platform.system() not in ['Linux', 'Windows'] or platform.machine().lower() not in ['x86_64', 'amd64']:
        raise ValueError('Native Linux/Windows x64 required')
    out.mkdir(parents=True, exist_ok=True)
    shutil.copytree(ROOT / 'validation/public-preview', out / 'consumer', ignore=shutil.ignore_patterns('bin', 'obj', 'target', 'Cargo.lock'))
    rust, dotnet = out / 'consumer/rust', out / 'consumer/dotnet'
    project = dotnet / 'Preview.csproj'
    project.write_text(project.read_text().replace('net8.0', args.framework))
    env = dict(os.environ, CARGO_HOME=str(out / 'cargo-home'), CARGO_TARGET_DIR=str(out / 'cargo-target'),
               NUGET_PACKAGES=str(out / 'nuget-cache'), NUGET_HTTP_CACHE_PATH=str(out / 'nuget-http-cache'),
               DOTNET_CLI_HOME=str(out / 'dotnet-home'), DOTNET_NOLOGO='1', DOTNET_CLI_TELEMETRY_OPTOUT='1')
    records = []

    def sha(file):
        return hashlib.sha256(file.read_bytes()).hexdigest()

    def run(name, command, cwd):
        print(name, flush=True)
        log = out / (name + '.log')
        with log.open('w') as stream:
            result = subprocess.run(list(map(str, command)), cwd=cwd, env=env, stdout=stream, stderr=subprocess.STDOUT)
        records.append(dict(name=name, command=list(map(str, command)), exitCode=result.returncode, log=log.name, sha256=sha(log)))
        (out / 'execution.json').write_text(json.dumps(records, indent=2) + '\n')
        text = log.read_text(errors='replace')
        if result.returncode or re.search(r'\bwarning IL\d+\b', text, re.I):
            raise RuntimeError(name + ' failed:\n' + text[-8000:])
        return text

    for features in [[], ['--features', 'async']]:
        suffix = 'async' if features else 'sync'
        output = run('rust-' + suffix, ['cargo', 'run', '--release'] + features, rust)
        if 'PASS public Rust sync' not in output or (features and 'PASS public Rust native async' not in output):
            raise ValueError('Expected public Rust execution markers')
        run('rust-' + suffix + '-clippy', ['cargo', 'clippy', '--release'] + features + ['--', '-D', 'warnings'], rust)
    metadata = json.loads(run('cargo-metadata', ['cargo', 'metadata', '--format-version', '1', '--features', 'async'], rust))
    for package in metadata['packages']:
        if package['name'].startswith('instrument-') and package['name'] != 'instrument-public-preview':
            if package.get('source') != 'registry+https://github.com/rust-lang/crates.io-index' or package['version'] != '0.2.0-alpha.1':
                raise ValueError('Non-public or wrong-version implementation: ' + package['name'])
    rid = 'win-x64' if os.name == 'nt' else 'linux-x64'
    native = out / 'native'
    run('native-publish', ['dotnet', 'publish', project.name, '-c', 'Release', '-r', rid, '--self-contained', 'true',
                           '--configfile', dotnet / 'NuGet.Config', '-o', native], dotnet)
    assets = json.loads((dotnet / 'obj/project.assets.json').read_text())
    if any(value['type'] != 'package' for value in assets['libraries'].values()):
        raise ValueError('Consumer must not reference source projects')
    binary = native / ('Preview.exe' if os.name == 'nt' else 'Preview')
    data = binary.read_bytes()
    if os.name == 'nt':
        offset = struct.unpack_from('<I', data, 0x3c)[0]
        if data[offset:offset+4] != b'PE\0\0' or struct.unpack_from('<H', data, offset+4)[0] != 0x8664 or struct.unpack_from('<II', data, offset+24+112+14*8) != (0, 0):
            raise ValueError('Expected x64 native PE without CLR header')
    elif data[:5] != b'\x7fELF\x02' or struct.unpack_from('<H', data, 18)[0] != 62:
        raise ValueError('Expected x64 native ELF')
    for name in ['native-execute', 'native-repeat']:
        if 'PASS public C# four frontends' not in run(name, [binary], out):
            raise ValueError('Missing public C# execution marker')
    packages = []
    for p in tomllib.loads((rust / 'Cargo.lock').read_text())['package']:
        if p['name'].startswith('instrument-') and p['name'] != 'instrument-public-preview':
            packages.append(dict(ecosystem='Cargo', name=p['name'], version=p['version'], sha256=p['checksum']))
    for key in assets['libraries']:
        name, version = key.split('/')
        if name.startswith('InstrumentComponents'):
            if version != '0.2.0-alpha.1':
                raise ValueError('Unexpected public NuGet version: ' + key)
            archive = Path(env['NUGET_PACKAGES']) / name.lower() / version / (name.lower() + '.' + version + '.nupkg')
            packages.append(dict(ecosystem='NuGet', name=name, version=version, sha256=sha(archive)))
    (out / 'proof.json').write_text(json.dumps(dict(mode='fresh-public-registry', framework=args.framework, rid=rid,
        nativeExecuted=True, nativeSha256=sha(binary), hardwareAccessed=False, packages=packages,
        sourceFiles={str(p.relative_to(ROOT)): sha(p) for p in (ROOT / 'validation/public-preview').rglob('*') if p.is_file() and not {'bin', 'obj', 'target'} & set(p.parts)}), indent=2) + '\n')
    print('PASS fresh public-package getting-started example, four frontends and Native AOT')


if __name__ == '__main__':
    main()
