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
import xml.etree.ElementTree as ET

from qualification_logs import execute_logged

ROOT = Path(__file__).resolve().parents[1]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output', type=Path)
    parser.add_argument('--check', action='store_true', help='Check candidate version-only declarations without restoring unpublished packages')
    parser.add_argument('--feeds', type=Path, help='Checksummed candidate composition feeds, not public registry evidence')
    parser.add_argument('--upgrade-from', choices=['0.2.0-alpha.1'])
    parser.add_argument('--baseline', action='store_true', help=argparse.SUPPRESS)
    parser.add_argument('--framework', choices=['net8.0', 'net10.0'], default='net8.0')
    parser.add_argument('--source-root', type=Path, default=ROOT, help='Immutable sample/payload checkout when the qualification helper is corrected separately')
    args = parser.parse_args()
    root = args.source_root.resolve()
    version = '0.2.0-alpha.1' if args.baseline else json.loads((root / 'spec/driver-sdk-release.json').read_text())['version']
    sample = root / ('validation/upgrade-from-0.2' if args.baseline else 'validation/public-preview')
    manifest = tomllib.loads((sample / 'rust/Cargo.toml').read_text())
    for name, value in manifest['dependencies'].items():
        if name.startswith('instrument-'):
            requirement = value if isinstance(value, str) else value['version']
            if requirement != '=' + version or isinstance(value, dict) and any(k in value for k in ['path', 'git']):
                raise ValueError('Wrong Rust preview dependency: ' + name)
    for ref in ET.parse(sample / 'dotnet/Preview.csproj').findall('.//PackageReference'):
        if ref.attrib['Version'] != '[' + version + ']':
            raise ValueError('Wrong NuGet preview dependency')
    if args.check:
        print('PASS version-only preview declarations: ' + version)
        return
    if args.output is None:
        parser.error('--output required for execution')
    out = args.output.resolve()
    if any(out == source or source in out.parents or out in source.parents for source in [root, ROOT]):
        raise ValueError('Use a separate empty output directory outside the checkout')
    dirty = {str(source): subprocess.check_output(['git', 'status', '--porcelain'], cwd=source, text=True).strip() for source in {root, ROOT}}
    if any(dirty.values()):
        raise ValueError('Committed payload and qualification helper required: ' + json.dumps(dirty))
    if out.exists() and any(out.iterdir()):
        raise ValueError('Use an empty output directory')
    if platform.system() not in ['Linux', 'Windows'] or platform.machine().lower() not in ['x86_64', 'amd64']:
        raise ValueError('Native Linux/Windows x64 required')
    out.mkdir(parents=True, exist_ok=True)
    shutil.copytree(sample, out / 'consumer', ignore=shutil.ignore_patterns('bin', 'obj', 'target', 'Cargo.lock'))
    rust, dotnet = out / 'consumer/rust', out / 'consumer/dotnet'
    project = dotnet / 'Preview.csproj'
    project.write_text(project.read_text().replace('net8.0', args.framework))
    env = dict(os.environ, CARGO_HOME=str(out / 'cargo-home'), CARGO_TARGET_DIR=str(out / 'cargo-target'),
               NUGET_PACKAGES=str(out / 'nuget-cache'), NUGET_HTTP_CACHE_PATH=str(out / 'nuget-http-cache'),
               DOTNET_CLI_HOME=str(out / 'dotnet-home'), DOTNET_NOLOGO='1', DOTNET_CLI_TELEMETRY_OPTOUT='1')
    mode = 'candidate-development-feed' if args.feeds else 'fresh-public-registry'
    if args.feeds:
        feed_root = args.feeds.resolve()
        registry = feed_root / 'cargo-registry'
        # A fresh package-only consumer, without path/project dependencies.
        Path(env['CARGO_HOME']).mkdir()
        (Path(env['CARGO_HOME']) / 'config.toml').write_text(
            '[source.crates-io]\nreplace-with="candidate"\n[source.candidate]\ndirectory=' + json.dumps(str(registry)) + '\n')
        shutil.copy2(feed_root / 'NuGet.Config', dotnet / 'NuGet.Config')
    records = []

    def sha(file):
        return hashlib.sha256(file.read_bytes()).hexdigest()

    def run(name, command, cwd, structured_output=False):
        print(name, flush=True)
        phase = execute_logged(name, command, cwd=cwd, env=env, output=out, structured_output=structured_output)
        records.append(phase)
        (out / 'execution.json').write_text(json.dumps(records, indent=2) + '\n', encoding='utf-8')
        text = (out / phase['log']).read_text(encoding='utf-8', errors='replace')
        diagnostic = (out / phase['stderrLog']).read_text(encoding='utf-8', errors='replace') if 'stderrLog' in phase else ''
        if phase['exitCode'] or re.search(r'\bwarning IL\d+\b', text + diagnostic, re.I):
            raise RuntimeError(name + ' failed:\n' + (text + diagnostic)[-8000:])
        return text

    for features in [[], ['--features', 'async']]:
        suffix = 'async' if features else 'sync'
        output = run('rust-' + suffix, ['cargo', 'run', '--release'] + features, rust)
        if ('PASS public Rust sync' if args.baseline else 'PASS preview Rust sync') not in output or (features and ('PASS public Rust native async' if args.baseline else 'PASS preview Rust native async') not in output):
            raise ValueError('Expected Rust execution markers')
        run('rust-' + suffix + '-clippy', ['cargo', 'clippy', '--release'] + features + ['--', '-D', 'warnings'], rust)
    metadata = json.loads(run('cargo-metadata', ['cargo', 'metadata', '--format-version', '1', '--features', 'async'], rust, structured_output=True))
    for package in metadata['packages']:
        if package['name'].startswith('instrument-') and package['name'] != 'instrument-public-preview':
            if (not args.feeds and package.get('source') != 'registry+https://github.com/rust-lang/crates.io-index') or package['version'] != version:
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
        if ('PASS public C# four frontends' if args.baseline else 'PASS preview C# four frontends') not in run(name, [binary], out):
            raise ValueError('Missing public C# execution marker')
    packages = []
    for p in tomllib.loads((rust / 'Cargo.lock').read_text())['package']:
        if p['name'].startswith('instrument-') and p['name'] != 'instrument-public-preview':
            if args.feeds:
                checksum = json.loads((registry / (p['name'] + '-' + p['version']) / '.cargo-checksum.json').read_text())['package']
                if checksum != p['checksum']:
                    raise ValueError('Candidate checksum mismatch')
                packages.append(dict(ecosystem='Cargo', name=p['name'], version=p['version'], candidateArchiveSha256=checksum))
            else:
                archives = list(Path(env['CARGO_HOME']).glob('registry/cache/*/' + p['name'] + '-' + p['version'] + '.crate'))
                if len(archives) != 1 or sha(archives[0]) != p['checksum']:
                    raise ValueError('Actual installed crate archive differs from Cargo.lock')
                packages.append(dict(ecosystem='Cargo', name=p['name'], version=p['version'], archiveSha256=sha(archives[0]), registryChecksum=p['checksum']))
    for key in assets['libraries']:
        name, version = key.split('/')
        if name.startswith('InstrumentComponents'):
            if version != ('0.2.0-alpha.1' if args.baseline else json.loads((root / 'spec/driver-sdk-release.json').read_text())['version']):
                raise ValueError('Unexpected public NuGet version: ' + key)
            archive = Path(env['NUGET_PACKAGES']) / name.lower() / version / (name.lower() + '.' + version + '.nupkg')
            packages.append(dict(ecosystem='NuGet', name=name, version=version, sha256=sha(archive)))
    (out / 'proof.json').write_text(json.dumps(dict(mode=mode, framework=args.framework, rid=rid, payloadSourceCommit=subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=root, text=True).strip(), helperSourceCommit=subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=ROOT, text=True).strip(), helperSha256=sha(Path(__file__)), loggerSha256=sha(ROOT/'tools/qualification_logs.py'),
        nativeExecuted=True, nativeSha256=sha(binary), hardwareAccessed=False, packages=packages,
        sourceFiles={str(p.relative_to(root)): sha(p) for p in sample.rglob('*') if p.is_file() and not {'bin', 'obj', 'target'} & set(p.parts)}), indent=2) + '\n')
    if args.upgrade_from:
        before = out / 'upgrade-before'
        subprocess.run([os.sys.executable, __file__, '--baseline', '--output', str(before), '--framework', args.framework, '--source-root', str(root)], check=True)
        old = json.loads((before / 'proof.json').read_text())
        new = json.loads((out / 'proof.json').read_text())
        (out / 'upgrade.json').write_text(json.dumps(dict(fromVersion=args.upgrade_from, toVersion=json.loads((root / 'spec/driver-sdk-release.json').read_text())['version'],
            before=old, after=new, breakingChange='Replace generic 34401A factory with explicit qualified factory and commissioned endpoint'), indent=2) + '\n')
    print('PASS ' + mode + ' getting-started example, four frontends and Native AOT')


if __name__ == '__main__':
    main()
