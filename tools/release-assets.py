#!/usr/bin/env python3
"""Create a prerelease and upload assets without replacing existing bytes."""
import argparse
import hashlib
import json
from pathlib import Path
import subprocess
import tempfile


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--tag', required=True)
    parser.add_argument('--assets', required=True, type=Path)
    args = parser.parse_args()
    files = sorted(p for p in args.assets.iterdir() if p.is_file())
    if not files:
        raise ValueError('No release assets')
    current = subprocess.check_output(['git', 'rev-parse', 'HEAD'], text=True).strip()
    # SHA-pinned release checkouts do not necessarily contain the tag ref.
    subprocess.run(['git', 'fetch', '--no-tags', '--depth=1', 'origin',
                    'refs/tags/' + args.tag + ':refs/tags/' + args.tag], check=True)
    tagged = subprocess.check_output(['git', 'rev-parse', args.tag + '^{commit}'], text=True).strip()
    if current != tagged:
        raise ValueError('Release source must equal immutable tag')
    result = subprocess.run(['gh', 'release', 'view', args.tag, '--json', 'assets,tagName,isPrerelease'], capture_output=True, text=True)
    if result.returncode:
        # Distinguish a missing release from an authentication/network error.
        repo = subprocess.check_output(['gh', 'repo', 'view', '--json', 'nameWithOwner', '--jq', '.nameWithOwner'], text=True).strip()
        probe = subprocess.run(['gh', 'api', 'repos/' + repo + '/releases/tags/' + args.tag], capture_output=True, text=True)
        if 'HTTP 404' not in probe.stderr:
            raise RuntimeError('Cannot inspect release state')
        subprocess.run(['gh', 'release', 'create', args.tag, '--verify-tag', '--prerelease', '--generate-notes', '--notes',
            'Hardware-unvalidated software preview. Device implementations are independently distributed. See qualification and commissioning limits in the source documentation.'], check=True)
        existing = {}
    else:
        release = json.loads(result.stdout)
        if not release['isPrerelease']:
            raise ValueError('Expected a prerelease')
        existing = {a['name']: a for a in release['assets']}
    records = []
    with tempfile.TemporaryDirectory() as directory:
        for file in files:
            expected = hashlib.sha256(file.read_bytes()).hexdigest()
            if file.name in existing:
                subprocess.run(['gh', 'release', 'download', args.tag, '--pattern', file.name, '--dir', directory], check=True)
                if hashlib.sha256((Path(directory) / file.name).read_bytes()).hexdigest() != expected:
                    raise ValueError('Immutable GitHub asset mismatch: ' + file.name)
            else:
                # No --clobber: concurrent duplicates fail rather than overwrite.
                subprocess.run(['gh', 'release', 'upload', args.tag, str(file)], check=True)
            records.append(dict(name=file.name, sha256=expected, sourceCommit=current, tag=args.tag))
    Path('release-assets-proof.json').write_text(json.dumps(records, indent=2) + '\n')


if __name__ == '__main__':
    main()
