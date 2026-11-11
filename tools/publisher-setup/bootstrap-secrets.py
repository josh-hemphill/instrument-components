#!/usr/bin/env python3
"""Plan first-publication secret provisioning for the six exact driver repositories.

Requires separately approved releases before use. No tags or packages created.
Token comes only from CARGO_BOOTSTRAP_TOKEN, never command-line arguments.
Existing secret values are preserved; they cannot be inspected or compared.
"""
import argparse,json,os,subprocess
from pathlib import Path
from provision import load
ROOT=Path(__file__).resolve().parents[2]
SECRET='CARGO_BOOTSTRAP_TOKEN'
def run(manifest,apply=False,remove=False,command=subprocess.run):
 repos=sorted({p['repository_owner']+'/'+p['repository_name'] for p in manifest['crates']});results=[]
 token=os.environ.get(SECRET,'')
 if apply and not remove and (not token or any(c.isspace() for c in token)):raise ValueError('Provide the short-lived first-publication token through the environment secret, never chat or arguments')
 for repo in repos:
  if not apply:results.append(dict(repository=repo,secret=SECRET,status='WOULD_REMOVE' if remove else 'WOULD_CREATE_IF_MISSING'));continue
  response=command(['gh','secret','list','--repo',repo,'--json','name'],capture_output=True,text=True)
  if response.returncode:raise ValueError('GitHub secret metadata access required for '+repo)
  exists=any(row['name']==SECRET for row in json.loads(response.stdout))
  if exists and not remove:results.append(dict(repository=repo,status='EXISTING_SECRET_PRESERVED'));continue
  if not exists and remove:results.append(dict(repository=repo,status='ALREADY_ABSENT'));continue
  args=['gh','secret','delete',SECRET,'--repo',repo] if remove else ['gh','secret','set',SECRET,'--repo',repo]
  response=command(args,input=None if remove else token,capture_output=True,text=True)
  if response.returncode:raise ValueError('GitHub secret update failed for '+repo+'; prior completed repositories remain changed')
  results.append(dict(repository=repo,status='REMOVED' if remove else 'CREATED'));print(json.dumps(results[-1]),flush=True)
 return results
if __name__=='__main__':
 p=argparse.ArgumentParser(description=__doc__);p.add_argument('--apply',action='store_true');p.add_argument('--remove',action='store_true',help='Remove only this temporary secret after all12 policies are configured');a=p.parse_args()
 try:print(json.dumps(run(load(ROOT/'spec/publishers.json'),a.apply,a.remove),indent=2))
 except ValueError as error:raise SystemExit(str(error))
