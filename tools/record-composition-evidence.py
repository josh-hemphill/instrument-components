#!/usr/bin/env python3
"""Join actual all-five CI artifacts to executable, phase and corpus digests."""
import argparse,hashlib,json,re,shutil,zipfile
from pathlib import Path
ROOT=Path(__file__).resolve().parents[1]
def sha(path):return hashlib.sha256(path.read_bytes()).hexdigest()
def main(inventory,lock):
 path=ROOT/'spec/device-execution-evidence.json';result=json.loads(path.read_text());result['cells']=[c for c in result['cells'] if c['kind']!='all-five-composition'];rows=json.loads(inventory.read_text());locked=json.loads(lock.read_text());artifacts=[]
 for row in rows:
  archive=Path(row['localArchive']);evidence=Path(row['localEvidence'])
  if 'sha256:'+sha(archive)!=row['digest'] or row['jobConclusion']!='success' or row['benchStepConclusion']!='success':raise ValueError('Incomplete composition/bench cell')
  proof=json.loads((evidence/'composition.json').read_text());execution=json.loads((evidence/'execution.json').read_text())
  if proof['lockSha256']!=sha(lock) or not proof['nativeExecuted'] or proof['mode']!='pinned-ci-artifact-bootstrap':raise ValueError('Unmatched current composition lock/source')
  rid=proof['rid'];framework=proof['framework'];phases={phase['name']:phase for phase in execution};required={'rust-default','rust-async','native-publish','native-execute','native-lifecycle-repeat','negative-native-warning'}
  if rid=='linux-x64':required|={'native-runtime-isolation','native-dependencies'}
  if not required<=set(phases):raise ValueError('Missing native composition phase')
  inputs=proof.get('referenceArtifactInputs',proof.get('inputs'));expected={package['package']:package['sha256'] for artifact in result['artifacts'] for package in artifact['driverPackages'] if artifact['native']['packed']['rid']==rid and artifact['native']['packed']['framework']==framework}
  if {p['package']:p['sha256'] for p in inputs}!=expected:raise ValueError('Composed driver packages differ from independent verified packages')
  with zipfile.ZipFile(archive) as z:
   binaries=[name for name in z.namelist() if name.endswith('/native/'+('Composition.exe' if rid=='win-x64' else 'Composition'))]
   if len(binaries)!=1 or hashlib.sha256(z.read(binaries[0])).hexdigest()!=proof['sha256']:raise ValueError('Composed native executable hash mismatch')
  logs=[]
  for name in sorted(required):
   phase=phases[name];source=evidence/phase['log'];text=source.read_text(errors='replace')
   if sha(source)!=phase['sha256'] or (phase['exitCode']==0 if phase['expectedFailure'] else phase['exitCode']!=0):raise ValueError('Composition phase failed or log drifted')
   if name=='negative-native-warning' and not all(re.search(r'\berror '+d+r'\b',text) for d in ['IL2026','IL3050']):raise ValueError('Both intended negative diagnostics required')
   if name in ['rust-default','rust-async','native-execute','native-lifecycle-repeat','native-runtime-isolation']:
    if 'PASS all-five' not in text or any(str(len(corpus['caseIds']))+' independent cases' not in text for corpus in result['corpora'].values()):raise ValueError('Complete five-corpus execution marker missing')
   dest=ROOT/'validation/evidence/composition'/(rid+'-'+framework)/source.name;dest.parent.mkdir(parents=True,exist_ok=True);shutil.copyfile(source,dest)
   logs.append(dict(phase=name,log=str(dest.relative_to(ROOT)),logSha256=phase['sha256'],exitCode=phase['exitCode'],expectedFailure=phase['expectedFailure']))
  run=row['workflow_run']['id'];repo='josh-hemphill/instrument-components-keysight';sourceCommit=row['workflow_run']['head_sha'];url='https://github.com/'+repo+'/actions/runs/'+str(run)+'/artifacts/'+str(row['id'])
  cell=dict(id='composition:'+rid+':'+framework,kind='all-five-composition',rid=rid,framework=framework,status='PASS',sourceCommit=sourceCommit,runId=run,jobId=row['jobId'],artifactId=row['id'],artifactDigest=row['digest'],url=url,lockSha256=sha(lock),sdkSourceCommit=locked['sdkCommit'],driverSourceCommits={d['vendor']:d['sourceCommit'] for d in locked['drivers']},corpusIds=sorted(result['corpora']),frontends=['Sync','NativeAsync','WorkerAsync','WorkerBlocking'],rustFeatures=['default','async'],diagnostics=['IL2026','IL3050'],isolationExecuted=rid=='linux-x64',benchSoftwareVerified=True,nativeSha256=proof['sha256'],phaseLogs=logs,log=logs[0]['log'],logSha256=logs[0]['logSha256'])
  result['cells'].append(cell);artifacts.append(dict(id=row['id'],digest=row['digest'],sourceCommit=sourceCommit,runId=run,url=url,proof=proof))
 if len(rows)!=4:raise ValueError('Four composition artifacts required')
 result['compositionArtifacts']=artifacts;path.write_text(json.dumps(result,indent=2,sort_keys=True)+'\n');print('Verified four native all-five/bench software cells, both diagnostic gates and exact package/phase/binary joins')
if __name__=='__main__':
 p=argparse.ArgumentParser(description=__doc__);p.add_argument('--artifact-inventory',type=Path,required=True);p.add_argument('--lock',type=Path,required=True);a=p.parse_args();main(a.artifact_inventory,a.lock)
