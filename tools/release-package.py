#!/usr/bin/env python3
"""Publish one immutable package, or verify an identical partial-release retry.

NuGet repository signatures and ZIP container timestamps are excluded from the
canonical payload identity. All other names and bytes (including PDB/source
metadata) must match. Rust uses the complete registry archive SHA256. Mismatches
stop: this tool never overwrites, unlists, yanks, or changes a release tag.
"""
import argparse
import hashlib
import io
import json
import os
from pathlib import Path
import subprocess
import time
import urllib.error
import urllib.request
import xml.etree.ElementTree as ET
import zipfile


def digest(data):
    return hashlib.sha256(data).hexdigest()


def nuget_payload(data):
    with zipfile.ZipFile(io.BytesIO(data)) as archive:
        names = archive.namelist()
        if len(names) != len(set(names)):
            raise ValueError('Duplicate NuGet ZIP member')
        result = hashlib.sha256()
        for name in sorted(names):
            if name == '.signature.p7s' or name.endswith('/'):
                continue
            content = archive.read(name)
            key = name.encode('utf-8')
            result.update(len(key).to_bytes(8, 'big'))
            result.update(key)
            result.update(len(content).to_bytes(8, 'big'))
            result.update(content)
        return result.hexdigest()


def fetch(url):
    try:
        with urllib.request.urlopen(urllib.request.Request(url, headers={'User-Agent': 'instrument-components-release/1'}), timeout=60) as response:
            data = response.read(512 * 1024 * 1024 + 1)
            if len(data) > 512 * 1024 * 1024:
                raise ValueError('Excessive package archive')
            return data
    except urllib.error.HTTPError as error:
        if error.code == 404:
            return None
        raise


def index_available(name, version, crate):
    if crate:
        key = name.lower()
        prefix = ('1/' if len(key) == 1 else '2/' if len(key) == 2 else '3/' + key[0] + '/' if len(key) == 3 else key[:2] + '/' + key[2:4] + '/')
        data = fetch('https://index.crates.io/' + prefix + key)
        return data is not None and any(row.get('vers') == version and not row.get('yanked')
            for row in map(json.loads, data.decode().splitlines()))
    data = fetch('https://api.nuget.org/v3-flatcontainer/' + name.lower() + '/index.json')
    return data is not None and version.lower() in json.loads(data)['versions']


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    group = parser.add_mutually_exclusive_group(required=True)
    group.add_argument('--crate', help='Workspace package ID')
    group.add_argument('--nuget', type=Path, help='Already packed NuGet archive')
    parser.add_argument('--output', type=Path, default=Path('publication-proof'))
    parser.add_argument('--timeout', type=int, default=900)
    args = parser.parse_args()
    source = subprocess.check_output(['git', 'rev-parse', 'HEAD'], text=True).strip()
    if subprocess.check_output(['git', 'status', '--porcelain', '--untracked-files=no'], text=True).strip():
        raise ValueError('Publishing requires an unchanged reviewed source tree')
    if args.crate:
        metadata = json.loads(subprocess.check_output(['cargo', 'metadata', '--no-deps', '--format-version', '1'], text=True))
        package = next(p for p in metadata['packages'] if p['name'] == args.crate)
        name, version = package['name'], package['version']
        if not Path('Cargo.lock').exists():
            subprocess.run(['cargo', 'generate-lockfile'], check=True)
        subprocess.run(['cargo', 'package', '--locked', '-p', name], check=True)
        archive = Path(metadata['target_directory']) / 'package' / (name + '-' + version + '.crate')
        url = 'https://static.crates.io/crates/' + name + '/' + name + '-' + version + '.crate'
    else:
        archive = args.nuget.resolve()
        with zipfile.ZipFile(archive) as packed:
            manifest = ET.fromstring(packed.read(next(n for n in packed.namelist() if n.endswith('.nuspec'))))
            name = manifest.findtext('.//{*}id')
            version = manifest.findtext('.//{*}version')
        url = 'https://api.nuget.org/v3-flatcontainer/' + name.lower() + '/' + version.lower() + '/' + name.lower() + '.' + version.lower() + '.nupkg'
    local = archive.read_bytes()
    identity = digest(local) if args.crate else nuget_payload(local)
    args.output.mkdir(parents=True, exist_ok=True)
    # Retain the original local archive and preflight before attempting publication.
    original = args.output / archive.name
    if original.exists() and original.read_bytes() != local:
        raise ValueError('Original publication archive changed')
    original.write_bytes(local)
    proof = dict(package=name, version=version, sourceCommit=source, archive=archive.name,
                 archiveSha256=digest(local), canonicalPayloadSha256=identity,
                 identityRule='archive-sha256' if args.crate else 'zip-member-names-and-bytes-excluding-root-nuget-signature',
                 registryUrl=url, stage='preflight', hardwareValidated=False)
    record = args.output / (name + '.json')
    def save():
        record.write_text(json.dumps(proof, indent=2) + '\n')
    save()
    remote = fetch(url)
    if remote is None:
        proof['stage'] = 'publishing'
        save()
        if args.crate:
            subprocess.run(['cargo', 'publish', '--locked', '-p', name], check=True)
        else:
            # Do not expose the API key in logs or persist it in the proof.
            key = os.environ['NUGET_API_KEY']
            result = subprocess.run(['dotnet', 'nuget', 'push', str(archive), '--source', 'https://api.nuget.org/v3/index.json', '--api-key', key])
            if result.returncode:
                raise RuntimeError('NuGet publication failed; inspect package state before retrying')
        proof['stage'] = 'waiting-for-registry'
        save()
        deadline = time.monotonic() + args.timeout
        while time.monotonic() < deadline:
            remote = fetch(url)
            if remote is not None:
                break
            time.sleep(5)
        if remote is None:
            raise RuntimeError('Registry availability timeout; version may already exist; retry identity check')
    remote_identity = digest(remote) if args.crate else nuget_payload(remote)
    proof.update(remoteArchiveSha256=digest(remote), remoteCanonicalPayloadSha256=remote_identity)
    if remote_identity != identity:
        proof['stage'] = 'identity-mismatch'
        save()
        raise ValueError('Immutable version payload mismatch; stop and select a new version after investigation')
    proof['stage'] = 'waiting-for-package-index'
    save()
    deadline = time.monotonic() + args.timeout
    while not index_available(name, version, bool(args.crate)):
        if time.monotonic() >= deadline:
            raise RuntimeError('Exact non-yanked package version not yet visible to dependency resolution')
        time.sleep(5)
    proof['indexVersionVerified'] = True
    proof['stage'] = 'available-and-identity-verified'
    save()
    print('PASS immutable registry identity and dependency availability: ' + name + '@' + version)


if __name__ == '__main__':
    main()
