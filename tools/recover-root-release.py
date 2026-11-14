#!/usr/bin/env python3
"""Requalify published root packages, then publish only the original TapPackage."""
import argparse,hashlib,json,os,re,subprocess,sys,tomllib,zipfile
from pathlib import Path,PurePosixPath
ROOT=Path(__file__).resolve().parents[1]

def sha(path):return hashlib.sha256(path.read_bytes()).hexdigest()

def validate_producer(run,jobs,artifacts,*,repository,source,tag):
    if run['repository']['full_name']!=repository or run['event']!='push' or run['path']!='.github/workflows/release.yml' or run['head_sha']!=source or run['head_branch']!=tag or run['status']!='completed':
        raise ValueError('Expected the exact completed root tag producer')
    ci={'Tokio feature check','Cross-compile check','Package instrument-core','Rust 1.85 minimum'}|{'Test ('+os_name+')' for os_name in ['ubuntu-latest','windows-latest','macos-latest']}|{'Driver SDK and native package proofs / SDK '+os_name+' / '+tfm for os_name in ['ubuntu-latest','windows-latest'] for tfm in ['net8.0','net10.0']}
    dotnet={'Shared data drift check','Build and test (mock)'}|{'.NET 10 compatibility ('+os_name+')' for os_name in ['ubuntu-latest','windows-latest']}|{'Build VISA package ('+os_name+')' for os_name in ['Linux','Windows']}|{'OpenTAP package ('+tfm+')' for tfm in ['net9.0','net10.0']}
    required={'Validate release versions','Publish to NuGet','Publish to crates.io','Build OpenTAP release artifact'}|{'CI gate / '+n for n in ci}|{'.NET gate / '+n for n in dotnet}
    deferred={'GitHub Release'}|{'Public root packages ('+os_name+', '+tfm+')' for os_name in ['ubuntu-latest','windows-latest'] for tfm in ['net8.0','net10.0']}
    if len(jobs)!=28 or {j['name'] for j in jobs}!=required|deferred:
        raise ValueError('Incomplete or unexpected original root job coverage')
    if any(j['status']!='completed' or j['name'] in required and j['conclusion']!='success' for j in jobs):
        raise ValueError('All original qualification, registry and TapPackage jobs must pass')
    selected={}
    for name in ['opentap-package','opentap-release-proof']+['driver-sdk-'+os_name+'-'+tfm for os_name in ['ubuntu-latest','windows-latest'] for tfm in ['net8.0','net10.0']]:
        matches=[a for a in artifacts if a['name']==name]
        if len(matches)!=1:raise ValueError('Missing or ambiguous original artifact: '+name)
        a=matches[0]
        if a['expired'] or a['workflow_run']['id']!=run['id'] or a['workflow_run']['head_sha']!=source or not re.fullmatch(r'sha256:[0-9a-f]{64}',a['digest']):raise ValueError('Original artifact identity mismatch')
        selected[name]=a
    return selected

def extract_checked(archive,target):
    with zipfile.ZipFile(archive) as z:
        if len(z.namelist())!=len(set(z.namelist())) or len(z.infolist())>10000 or sum(i.file_size for i in z.infolist())>512*1024*1024:raise ValueError('Unexpected recovery archive size/member set')
        for info in z.infolist():
            p=PurePosixPath(info.filename)
            if p.is_absolute() or '..' in p.parts or ':' in info.filename or '\\' in info.filename:raise ValueError('Unsafe recovery archive path')
            path=target/Path(*p.parts)
            if info.is_dir():path.mkdir(parents=True,exist_ok=True)
            else:path.parent.mkdir(parents=True,exist_ok=True);path.write_bytes(z.read(info))

def main():
    p=argparse.ArgumentParser(description=__doc__)
    p.add_argument('--mode',required=True,choices=['qualify','assets'])
    p.add_argument('--tag',required=True);p.add_argument('--source',required=True)
    p.add_argument('--producer-run-id',required=True,type=int);p.add_argument('--output',required=True,type=Path)
    p.add_argument('--framework',choices=['net8.0','net10.0'])
    a=p.parse_args()
    if not re.fullmatch(r'v[0-9]+\.[0-9]+\.[0-9]+-alpha\.[0-9]+',a.tag) or not re.fullmatch(r'[0-9a-f]{40}',a.source) or a.producer_run_id<=0 or a.mode=='qualify' and not a.framework:raise ValueError('Explicit root alpha tag/source/producer and qualification framework required')
    out=a.output.resolve()
    if out==ROOT or ROOT in out.parents or out in ROOT.parents or out.exists() and any(out.iterdir()):raise ValueError('Separate empty recovery output required')
    out.mkdir(parents=True);repo=os.environ['GITHUB_REPOSITORY'];env=dict(os.environ,GH_REPO=repo)
    def api(path):return json.loads(subprocess.check_output(['gh','api','repos/'+repo+('/'+path if path else '')],env=env,text=True))
    run=api('actions/runs/'+str(a.producer_run_id));jobs=api('actions/runs/'+str(a.producer_run_id)+'/jobs?per_page=100')['jobs'];artifacts=api('actions/runs/'+str(a.producer_run_id)+'/artifacts?per_page=100')['artifacts']
    selected=validate_producer(run,jobs,artifacts,repository=repo,source=a.source,tag=a.tag)
    for name,value in [('producer',run),('producer-jobs',jobs),('producer-artifacts',selected)]:
        (out/(name+'.json')).write_text(json.dumps(value,indent=2)+'\n',encoding='utf-8')
    proof=dict(mode=a.mode,stage='producer-verified',sourceCommit=a.source,tag=a.tag,producerRunId=a.producer_run_id,producerConclusion=run['conclusion'],helperCommit=subprocess.check_output(['git','rev-parse','HEAD'],cwd=ROOT,text=True).strip(),helperSha256=sha(ROOT/'tools/validate-root-packages.py'),recoveryRunId=os.environ.get('GITHUB_RUN_ID'),actor=os.environ.get('GITHUB_ACTOR'),hardwareValidated=False)
    def save():(out/'root-recovery.json').write_text(json.dumps(proof,indent=2)+'\n',encoding='utf-8')
    save()
    subprocess.run(['git','fetch','--no-tags','origin',a.source],cwd=ROOT,check=True)
    subprocess.run(['git','merge-base','--is-ancestor',a.source,'refs/remotes/origin/'+api('')['default_branch']],cwd=ROOT,check=True)
    payload=out/'payload';subprocess.run(['git','worktree','add','--detach',str(payload),a.source],cwd=ROOT,check=True)
    if tomllib.loads((payload/'Cargo.toml').read_text(encoding='utf-8'))['workspace']['package']['version']!=a.tag.removeprefix('v'):raise ValueError('Payload version differs from original root tag')
    def download(name):
        artifact=selected[name];archive=out/(name+'.zip')
        with archive.open('wb') as stream:subprocess.run(['gh','api','repos/'+repo+'/actions/artifacts/'+str(artifact['id'])+'/zip'],env=env,stdout=stream,check=True)
        if 'sha256:'+sha(archive)!=artifact['digest']:raise ValueError('Original artifact transfer checksum mismatch')
        target=out/name;target.mkdir();extract_checked(archive,target);return target
    if a.mode=='qualify':
        os_name='windows-latest' if os.name=='nt' else 'ubuntu-latest';sdk=download('driver-sdk-'+os_name+'-'+a.framework)
        subprocess.run([sys.executable,'-m','pip','install',str(payload/'tools/driver-tools')],check=True)
        command=[sys.executable,str(ROOT/'tools/validate-root-packages.py'),'--source-root',str(payload),'--sdk-proof',str(sdk),'--output',str(out/'public-root'),'--framework',a.framework,'--public-registry']
        with (out/'qualify.log').open('w',encoding='utf-8') as stream:result=subprocess.run(command,cwd=payload,env=env,stdout=stream,stderr=subprocess.STDOUT)
        proof.update(command=command,exitCode=result.returncode);save()
        if result.returncode:raise RuntimeError('Published root qualification failed; inspect retained log')
        public=json.loads((out/'public-root/proof.json').read_text(encoding='utf-8'))
        if public['sourceCommit']!=a.source or public['helperSourceCommit']!=proof['helperCommit'] or public['sourceDirty'] or public['helperSourceDirty'] or public['mode']!='fresh-public-registry':raise ValueError('Public proof source/helper identity mismatch')
        proof.update(stage='published-root-consumers-qualified',framework=a.framework,publicProofSha256=sha(out/'public-root/proof.json'));save()
    else:
        package=download('opentap-package');installed=download('opentap-release-proof')
        original=json.loads((installed/'proof.json').read_text(encoding='utf-8'));expected='InstrumentComponents.OpenTap.'+a.tag.removeprefix('v')+'.TapPackage'
        files=[x for x in package.rglob('*') if x.is_file()]
        if len(files)!=1 or files[0].name!=expected or sha(files[0])!=original['packageSha256'] or original['hardwareValidated'] or original['nativeAotClaim']:raise ValueError('Original TapPackage/install identity mismatch')
        for phase in json.loads((installed/'execution.json').read_text(encoding='utf-8')):
            if phase['exitCode'] or sha(installed/phase['log'])!=phase['sha256']:raise ValueError('Original TapPackage lifecycle proof mismatch')
        with zipfile.ZipFile(files[0]) as z:
            if any('visa' in n.lower() or 'drivers.' in n.lower() for n in z.namelist()):raise ValueError('Unexpected TapPackage closure')
            for name,digest in original['installedDllSha256'].items():
                if hashlib.sha256(z.read(name)).hexdigest()!=digest:raise ValueError('Original installed DLL differs from TapPackage')
        command=[sys.executable,str(ROOT/'tools/release-assets.py'),'--tag',a.tag,'--assets',str(package)]
        with (out/'publish-assets.log').open('w',encoding='utf-8') as stream:result=subprocess.run(command,cwd=payload,env=env,stdout=stream,stderr=subprocess.STDOUT)
        proof.update(command=command,exitCode=result.returncode);save()
        if result.returncode:raise RuntimeError('Original TapPackage publication failed; inspect retained log')
        (out/'release-assets-proof.json').write_bytes((payload/'release-assets-proof.json').read_bytes())
        page=api('releases/tags/'+a.tag)
        if page['draft'] or not page['prerelease'] or page['tag_name']!=a.tag:raise ValueError('Expected public root prerelease')
        (out/'release-page.json').write_text(json.dumps(page,indent=2)+'\n',encoding='utf-8');remote=out/'remote-assets';remote.mkdir()
        subprocess.run(['gh','release','download',a.tag,'--pattern',expected,'--dir',str(remote),'--repo',repo],env=env,check=True)
        if sha(remote/expected)!=original['packageSha256']:raise ValueError('Published TapPackage differs from original')
        proof.update(stage='original-tap-published-and-identity-verified',assetSha256=original['packageSha256'],releaseUrl=page['html_url']);save()
    print('PASS root recovery '+a.mode+'; original producer conclusion remains '+str(run['conclusion']))

if __name__=='__main__':main()
