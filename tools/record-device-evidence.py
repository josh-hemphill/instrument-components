#!/usr/bin/env python3
"""Index verified hosted driver artifacts and preserve their executed phase logs.

Developer-only evidence import. Core CI checks the committed result offline;
no external implementation or registry access is needed for a normal build.
"""
import argparse,hashlib,json,re,shutil,subprocess,zipfile
from pathlib import Path
ROOT=Path(__file__).resolve().parents[1]
def sha(path):return hashlib.sha256(path.read_bytes()).hexdigest()
def write(path,value):path.parent.mkdir(parents=True,exist_ok=True);path.write_text(json.dumps(value,indent=2,sort_keys=True)+'\n',encoding='utf-8')
def collect(inventory,parent):
 rows=json.loads(inventory.read_text());cells=[];corpora={};artifacts=[]
 for row in rows:
  vendor=row['vendor'];repo=parent/('instrument-components-'+vendor);evidence=Path(row['localEvidence']);archive=Path(row['localArchive']);commit=row['workflow_run']['head_sha']
  if 'sha256:'+sha(archive)!=row['digest']:raise ValueError('Archive transfer digest mismatch')
  def blob(file):return subprocess.check_output(['git','show',commit+':'+file],cwd=repo)
  cases=blob('cases.json');caseData=json.loads(cases);sourceHash=hashlib.sha256(cases).hexdigest();corpus='driver:'+vendor
  body=dict(sourceRepository='josh-hemphill/instrument-components-'+vendor,sourceCommit=commit,caseFile='cases.json',caseManifestSha256=sourceHash,caseIds=[c['id'] for c in caseData['cases']])
  if corpus in corpora and corpora[corpus]!=body:raise ValueError('Mixed source revisions in driver evidence')
  corpora[corpus]=body
  producer=re.search(r'source_ref: ([0-9a-f]{40})',blob('.github/workflows/ci.yml').decode())[1]
  execution=json.loads((evidence/'execution.json').read_text());packages=json.loads((evidence/'packages.json').read_text());native={mode:json.loads((evidence/(mode+'-native.json')).read_text()) for mode in ['generated','packed']}
  platform=native['packed']['rid'];framework=native['packed']['framework'];url='https://github.com/'+body['sourceRepository']+'/actions/runs/'+str(row['workflow_run']['id'])+'/artifacts/'+str(row['id'])
  with zipfile.ZipFile(archive) as packed:
   for package in packages['driverPackages']:
    names=[n for n in packed.namelist() if n.endswith('/'+package['file']) or n==package['file']]
    if len(names)!=1 or hashlib.sha256(packed.read(names[0])).hexdigest()!=package['sha256']:raise ValueError('Driver package digest mismatch')
   for mode,record in native.items():
    binaries=[n for n in packed.namelist() if n.endswith('/'+mode+'-native/'+('DriverProof.exe' if platform=='win-x64' else 'DriverProof'))]
    if len(binaries)!=1 or hashlib.sha256(packed.read(binaries[0])).hexdigest()!=record['sha256'] or not record['executed']:raise ValueError('Native executable digest mismatch')
  artifact=dict(repository=body['sourceRepository'],runId=row['workflow_run']['id'],sourceCommit=commit,artifactId=row['id'],artifactDigest=row['digest'],url=url,driverPackages=packages['driverPackages'],sdkSourceCommit=producer,sdkPackageHashes=packages['sdkPackages'],native=native)
  artifacts.append(artifact)
  for phase in execution:
   log=evidence/phase['log']
   if sha(log)!=phase['sha256']:raise ValueError('Phase log digest mismatch')
   negative=phase['expectedFailure']
   if (phase['exitCode']==0 if negative else phase['exitCode']!=0):raise ValueError('Unexpected phase exit')
   name=phase['name'];is_case=name.endswith(('-rust-default','-rust-async','-native-execute'))
   if not is_case and not negative:continue
   text=log.read_text(encoding='utf-8',errors='replace')
   diagnostics=[d for d in ['IL2026','IL3050'] if re.search(r'\berror '+d+r'\b',text)]
   if negative and 'IL2026' not in diagnostics:raise ValueError('No intended negative diagnostic')
   if is_case and ('PASS' not in text or str(len(caseData['cases']))+' independent cases' not in text):raise ValueError('No exact complete corpus pass marker')
   dest=ROOT/'validation/evidence/drivers'/vendor/(platform+'-'+framework)/log.name;dest.parent.mkdir(parents=True,exist_ok=True);shutil.copyfile(log,dest)
   frontends=['Sync'] if name.endswith('-rust-default') else ['Sync','NativeAsync','WorkerAsync','WorkerBlocking'] if is_case else []
   cell=dict(id=vendor+':'+platform+':'+framework+':'+name,kind='case-execution' if is_case else 'intended-negative-warning-gate',corpusId=corpus if is_case else None,rid=platform,framework=framework,language='C#' if 'native' in name else 'Rust',frontends=frontends,phase=name,artifactId=row['id'],runId=row['workflow_run']['id'],sourceCommit=commit,exitCode=phase['exitCode'],expectedFailure=negative,diagnostics=diagnostics,log=str(dest.relative_to(ROOT)).replace('\\','/'),logSha256=phase['sha256'],status='PASS' if is_case or set(diagnostics)=={'IL2026','IL3050'} else 'IL2026_VERIFIED_IL3050_PENDING')
   cells.append(cell)
 return dict(schemaVersion=1,mode='pinned-ci-artifact-bootstrap',corpora=corpora,artifacts=artifacts,cells=cells,publicRegistryStatus='PENDING_AUTHENTICATION_AND_SEPARATE_PUBLICATION_AUTHORIZATION',hardwareStatus='UNVERIFIED_NOT_RUN')
if __name__=='__main__':
 p=argparse.ArgumentParser(description=__doc__);p.add_argument('--artifact-inventory',type=Path,required=True);p.add_argument('--external-root',type=Path,default=ROOT.parent);a=p.parse_args();result=collect(a.artifact_inventory,a.external_root);write(ROOT/'spec/device-execution-evidence.json',result);print('Verified and indexed '+str(len(result['cells']))+' executed/negative cells from '+str(len(result['artifacts']))+' hosted artifacts')
