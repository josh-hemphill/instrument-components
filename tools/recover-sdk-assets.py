#!/usr/bin/env python3
"""Recover SDK assets from one qualified producer without rebuilding or republishing."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import tomllib
import zipfile

ROOT = Path(__file__).resolve().parents[1]

def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()

def validate_producer(run, jobs, artifacts, *, repository, source, tag):
    if run['repository']['full_name'] != repository or run['event'] != 'push' or run['path'] != '.github/workflows/driver-sdk-preview.yml':
        raise ValueError('Expected the SDK tag producer in this repository')
    if run['head_sha'] != source or run['head_branch'] != tag or run['status'] != 'completed':
        raise ValueError('Producer source/tag/state mismatch')
    required = {'validate', 'crates', 'nuget', 'tool-assets'} | {'conformance / SDK '+os_name+' / '+tfm for os_name in ['ubuntu-latest', 'windows-latest'] for tfm in ['net8.0', 'net10.0']}
    if len(jobs) != len(required)+1 or {j['name'] for j in jobs} != required | {'release'}:
        raise ValueError('Incomplete or unexpected SDK producer jobs')
    if any(j['status'] != 'completed' or (j['conclusion'] != 'success' if j['name'] in required else j['conclusion'] not in ['success', 'failure', 'cancelled']) for j in jobs):
        raise ValueError('Producer qualification/registry jobs must have succeeded')
    matches = [a for a in artifacts if a['name'] == 'sdk-tool-assets']
    if len(matches) != 1:
        raise ValueError('Expected one original SDK asset artifact')
    artifact = matches[0]
    if artifact['expired'] or artifact['workflow_run']['id'] != run['id'] or artifact['workflow_run']['head_sha'] != source or not re.fullmatch(r'sha256:[0-9a-f]{64}', artifact['digest']):
        raise ValueError('Original artifact source/digest mismatch')
    return artifact

def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('--tag', required=True)
    p.add_argument('--producer-run-id', required=True, type=int)
    p.add_argument('--source', required=True)
    p.add_argument('--output', required=True, type=Path)
    a = p.parse_args()
    if not re.fullmatch(r'driver-sdk-v[0-9]+\.[0-9]+\.[0-9]+-alpha\.[0-9]+', a.tag) or not re.fullmatch(r'[0-9a-f]{40}', a.source) or a.producer_run_id <= 0:
        raise ValueError('Explicit SDK alpha tag, full source SHA and producer run required')
    out = a.output.resolve()
    if out == ROOT or ROOT in out.parents or out in ROOT.parents or out.exists() and any(out.iterdir()):
        raise ValueError('Separate empty recovery output required')
    out.mkdir(parents=True)
    repo = os.environ['GITHUB_REPOSITORY']
    env = dict(os.environ, GH_REPO=repo)
    def api(path):
        return json.loads(subprocess.check_output(['gh', 'api', path], env=env, text=True))
    prefix = 'repos/'+repo
    run = api(prefix+'/actions/runs/'+str(a.producer_run_id))
    jobs = api(prefix+'/actions/runs/'+str(a.producer_run_id)+'/jobs?per_page=100')['jobs']
    artifacts = api(prefix+'/actions/runs/'+str(a.producer_run_id)+'/artifacts?per_page=100')['artifacts']
    artifact = validate_producer(run, jobs, artifacts, repository=repo, source=a.source, tag=a.tag)
    for name, record in [('producer', run), ('producer-jobs', jobs), ('producer-artifact', artifact)]:
        (out/(name+'.json')).write_text(json.dumps(record, indent=2)+'\n')
    proof = dict(stage='producer-verified', producerRunId=a.producer_run_id, producerConclusion=run['conclusion'], sourceCommit=a.source, tag=a.tag, artifactId=artifact['id'], artifactSha256=artifact['digest'], helperCommit=subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=ROOT, text=True).strip(), helperSha256=sha(ROOT/'tools/release-assets.py'), recoveryRunId=os.environ.get('GITHUB_RUN_ID'), actor=os.environ.get('GITHUB_ACTOR'), hardwareValidated=False)
    def save():
        (out/'recovery.json').write_text(json.dumps(proof, indent=2)+'\n')
    save()
    archive = out/'original-sdk-tool-assets.zip'
    with archive.open('wb') as stream:
        subprocess.run(['gh', 'api', prefix+'/actions/artifacts/'+str(artifact['id'])+'/zip'], env=env, stdout=stream, check=True)
    if 'sha256:'+sha(archive) != artifact['digest']:
        raise ValueError('Original artifact transfer checksum mismatch')
    subprocess.run(['git', 'fetch', '--no-tags', 'origin', a.source], cwd=ROOT, check=True)
    default_branch = api(prefix)['default_branch']
    subprocess.run(['git', 'merge-base', '--is-ancestor', a.source, 'refs/remotes/origin/'+default_branch], cwd=ROOT, check=True)
    payload = out/'payload'
    subprocess.run(['git', 'worktree', 'add', '--detach', str(payload), a.source], cwd=ROOT, check=True)
    subprocess.run([sys.executable, str(payload/'tools/validate-sdk-release.py'), '--tag', a.tag], cwd=payload, check=True)
    version = tomllib.loads((payload/'tools/driver-tools/pyproject.toml').read_text())['project']['version']
    wheel = 'instrument_driver_tools-'+version+'-py3-none-any.whl'
    assets = out/'assets';assets.mkdir()
    with zipfile.ZipFile(archive) as z:
        if len(z.namelist()) != 2 or set(z.namelist()) != {wheel, 'SHA256SUMS'}:
            raise ValueError('Unexpected original SDK asset closure')
        for name in z.namelist():
            (assets/name).write_bytes(z.read(name))
    parts = (assets/'SHA256SUMS').read_text().split()
    if parts != [sha(assets/wheel), wheel]:
        raise ValueError('Original wheel checksum mismatch')
    with zipfile.ZipFile(assets/wheel) as z:
        package = payload/'tools/driver-tools/src'
        expected = {str(path.relative_to(package)).replace('\\', '/'):path for path in (package/'instrument_driver_tools').rglob('*') if path.is_file() and '__pycache__' not in path.parts}
        if {n for n in z.namelist() if n.startswith('instrument_driver_tools/')} != set(expected):
            raise ValueError('Wheel source member set differs from qualified source')
        for name, path in expected.items():
            if z.read(name) != path.read_bytes():
                raise ValueError('Wheel source differs from qualified source: '+name)
    proof.update(stage='original-assets-source-verified', assets={path.name:sha(path) for path in assets.iterdir()});save()
    command = [sys.executable, str(ROOT/'tools/release-assets.py'), '--tag', a.tag, '--assets', str(assets)]
    with (out/'publish-assets.log').open('w') as stream:
        result = subprocess.run(command, cwd=payload, env=env, stdout=stream, stderr=subprocess.STDOUT)
    proof.update(command=command, exitCode=result.returncode);save()
    if result.returncode:
        raise RuntimeError('Recovery stopped; inspect retained asset publication log')
    receipt = payload/'release-assets-proof.json'
    (out/'release-assets-proof.json').write_bytes(receipt.read_bytes())
    page = api(prefix+'/releases/tags/'+a.tag)
    if page['draft'] or not page['prerelease'] or page['tag_name'] != a.tag:
        raise ValueError('Expected the public SDK prerelease page')
    (out/'release-page.json').write_text(json.dumps(page, indent=2)+'\n')
    downloaded = out/'remote-assets';downloaded.mkdir()
    for path in assets.iterdir():
        subprocess.run(['gh', 'release', 'download', a.tag, '--pattern', path.name, '--dir', str(downloaded), '--repo', repo], env=env, check=True)
        if sha(downloaded/path.name) != sha(path):
            raise ValueError('Downloaded release asset differs from original producer: '+path.name)
    proof.update(stage='original-assets-published-and-identity-verified', releaseAssetsProofSha256=sha(receipt), verifiedRemoteAssets={path.name:sha(path) for path in downloaded.iterdir()}, releaseUrl=page['html_url']);save()
    print('PASS original SDK assets recovered; original producer conclusion remains '+str(run['conclusion']))

if __name__ == '__main__':
    main()
