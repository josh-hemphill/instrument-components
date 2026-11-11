#!/usr/bin/env python3
"""Plan or provision exact trusted-publisher policies. Never publish a package.

Crates.io supports a scoped configuration token. NuGet account policy management
uses its authenticated UI session, retained only inside an ephemeral browser.
"""
import argparse
import json
import os
from pathlib import Path
import re
import sys
import tempfile
import urllib.error
import urllib.parse
import urllib.request

ROOT=Path(__file__).resolve().parents[2]
CRATES_API='https://crates.io/api/v1'
TOKEN_VARIABLE='CRATES_IO_TRUSTED_PUBLISHING_TOKEN'

class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self,*args,**kwargs):raise ValueError('Registry redirect refused; credentials were not forwarded')

class Crates:
    def __init__(self,token=None):self.token=token;self.opener=urllib.request.build_opener(NoRedirect())
    def request(self,path,payload=None):
        if not path.startswith('/') or path.startswith('//') or '\\' in path:raise ValueError('Invalid API path')
        headers={'User-Agent':'instrument-components publisher setup (https://github.com/josh-hemphill/instrument-components)','Accept':'application/json'}
        if self.token:headers['Authorization']='Bearer '+self.token
        data=None if payload is None else json.dumps(payload).encode()
        if data is not None:headers['Content-Type']='application/json'
        request=urllib.request.Request(CRATES_API+path,data=data,headers=headers)
        try:
            with self.opener.open(request,timeout=30) as response:return response.status,json.load(response)
        except urllib.error.HTTPError as error:return error.code,None # Never print auth headers or response internals.
    def exists(self,name):
        status,_=self.request('/crates/'+urllib.parse.quote(name,safe=''))
        if status not in [200,404]:raise ValueError(f'Crate lookup failed for {name}: HTTP {status}')
        return status==200
    def policies(self,name):
        path='/trusted_publishing/github_configs?'+urllib.parse.urlencode({'crate':name,'per_page':100});rows=[]
        for _ in range(20):
            status,data=self.request(path)
            if status!=200:return status,[]
            rows+=data['github_configs'];following=data['meta'].get('next_page')
            if not following:return 200,rows
            if not following.startswith('?') or '#' in following:raise ValueError('Unexpected registry pagination link')
            query=urllib.parse.parse_qs(following[1:],strict_parsing=True)
            if any(key not in {'crate','per_page','seek'} for key in query):raise ValueError('Unexpected registry pagination parameter')
            if query.get('crate',[name])!=[name]:raise ValueError('Registry pagination changed crate')
            query['crate']=[name];query['per_page']=['100']
            path='/trusted_publishing/github_configs?'+urllib.parse.urlencode(query,doseq=True)
        raise ValueError('Excessive registry pagination')
    def create(self,policy):return self.request('/trusted_publishing/github_configs',{'github_config':policy})

def load(path):
    manifest=json.loads(path.read_text(encoding='utf-8'))
    if manifest.get('schemaVersion')!=1:raise ValueError('Unsupported policy manifest')
    if not isinstance(manifest.get('githubOwnerId'),int) or isinstance(manifest['githubOwnerId'],bool) or manifest['githubOwnerId']<=0:raise ValueError('Expected numeric GitHub owner ID')
    seen=set()
    for policy in manifest['crates']:
        if set(policy)!={'crate','repository_owner','repository_name','workflow_filename','environment'}:raise ValueError('Unexpected crate policy field')
        if not re.fullmatch(r'[a-z][a-z0-9-]*',policy['crate']) or policy['crate'] in seen:raise ValueError('Duplicate/invalid crate name')
        if policy['repository_owner']!=manifest['githubOwner']:raise ValueError('Unexpected GitHub owner')
        seen.add(policy['crate']);validate_repository(policy['repository_owner'],policy['repository_name'],policy['workflow_filename'])
        if policy['environment'] is not None:raise ValueError('This setup expects no optional environment restriction')
    seen=set()
    for policy in manifest['nuget']:
        validate_repository(policy['repositoryOwner'],policy['repository'],policy['workflow'])
        if not policy['packages'] or any(not re.fullmatch(r'[A-Za-z][A-Za-z0-9.]*',name) for name in policy['packages']):raise ValueError('Exact NuGet package IDs required, no wildcards')
        if not re.fullmatch(r'[A-Za-z0-9_.-]+',policy['name']) or not re.fullmatch(r'[A-Za-z0-9_.-]+',policy['owner']):raise ValueError('Invalid NuGet policy name/owner')
        if any(not isinstance(policy.get(key),int) or isinstance(policy[key],bool) or policy[key]<=0 for key in ['repositoryId','repositoryOwnerId']):raise ValueError('Exact positive GitHub numeric IDs required')
        if len({name.lower() for name in policy['packages']})!=len(policy['packages']):raise ValueError('Duplicate NuGet package ID')
        key=(policy['owner'],policy['repositoryOwner'],policy['repository'],policy['workflow'])
        if key in seen:raise ValueError('Duplicate NuGet policy')
        seen.add(key)
    return manifest

def validate_repository(owner,repo,workflow):
    if not re.fullmatch(r'[A-Za-z0-9-]+',owner) or not re.fullmatch(r'[A-Za-z0-9_.-]+',repo) or not re.fullmatch(r'[A-Za-z0-9_.-]+\.ya?ml',workflow):raise ValueError('Invalid repository/workflow tuple')

def same_crate_policy(existing,desired):
    return all(existing.get(key)==value for key,value in desired.items() if key!='repository_owner') and existing.get('repository_owner','').lower()==desired['repository_owner'].lower()

def provision_crates(manifest,client,apply=False,progress=None):
    results=[]
    def record(row):
        results.append(row)
        if progress:progress(results)
    for policy in manifest['crates']:
        name=policy['crate']
        if not client.exists(name):record(dict(package=name,status='FIRST_PUBLICATION_REQUIRED'));continue
        status,existing=client.policies(name)
        if status in [401,403]:record(dict(package=name,status='AUTHENTICATION_REQUIRED'));continue
        if status!=200:raise ValueError(f'Policy lookup failed for {name}: HTTP {status}')
        matches=[row for row in existing if same_crate_policy(row,policy)]
        if matches:
            verified=any(row.get('repository_owner_id')==manifest['githubOwnerId'] for row in matches)
            record(dict(package=name,status='ALREADY_CONFIGURED' if verified else 'EXISTING_IDENTITY_REVIEW_REQUIRED'));continue
        if not apply:record(dict(package=name,status='WOULD_CREATE',policy=policy));continue
        if not client.token:record(dict(package=name,status='AUTHENTICATION_REQUIRED'));continue
        try:status,created=client.create(policy)
        except (OSError,ValueError) as error:
            record(dict(package=name,status='CREATION_OUTCOME_UNKNOWN'))
            raise ValueError('Registry request interrupted; inspect the saved report and rerun the lookup before retrying') from error
        row=created.get('github_config') if isinstance(created,dict) else None
        if not isinstance(row,dict):
            record(dict(package=name,status='CREATION_VERIFICATION_REQUIRED',httpStatus=status))
            return results
        if status!=200 or not same_crate_policy(row,policy) or row.get('repository_owner_id')!=manifest['githubOwnerId']:
            record(dict(package=name,status='CREATION_VERIFICATION_REQUIRED',httpStatus=status))
            return results
        record(dict(package=name,status='CREATED',policyId=created['github_config']['id']))
    return results

async def provision_nuget(manifest,apply,executable=None,progress=None):
    try:from playwright.async_api import async_playwright
    except ImportError:raise ValueError('Install optional browser dependency: python -m pip install playwright; python -m playwright install chromium')
    # No cookies/tokens are exported, logged, or committed. User signs in through
    # the normal visible Microsoft/NuGet login. The temporary browser closes after
    # the batch and its private profile directory is deleted.
    with tempfile.TemporaryDirectory(prefix='instrument-publisher-login-') as profile:
        async with async_playwright() as playwright:
            context=await playwright.chromium.launch_persistent_context(profile,headless=False,executable_path=executable)
            try:
                page=await context.new_page();await page.goto('https://www.nuget.org/account/TrustedPublishing')
                print('Complete NuGet sign-in in the visible browser. Waiting for the account policy editor; no packages will be published.',flush=True)
                await page.wait_for_function("location.origin==='https://www.nuget.org' && typeof initialData==='object' && Array.isArray(initialData.Policies) && typeof window.nuget?.addAjaxAntiForgeryToken==='function'",timeout=0)
                results=[];script=(Path(__file__).parent/'nuget-policy.js').read_text(encoding='utf-8')
                for policy in manifest['nuget']:
                    try:result=await page.evaluate(script,dict(policy=policy,apply=apply))
                    except Exception as error:
                        results.append(dict(policy=policy['name'],status='CREATION_OUTCOME_UNKNOWN' if apply else 'LOOKUP_FAILED'))
                        if progress:progress(results)
                        raise ValueError('Account-page request failed; inspect saved progress and the policy page before retrying') from error
                    results.append(result)
                    if progress:progress(results)
                    print(json.dumps(result),flush=True)
                    if result['status']=='CREATION_VERIFICATION_REQUIRED':return results
                    if result['status']=='CREATED':
                        await page.reload();await page.wait_for_function("typeof initialData==='object' && Array.isArray(initialData.Policies)")
                return results
            finally:await context.close()

def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--manifest',type=Path,default=ROOT/'spec/publishers.json')
    parser.add_argument('--service',choices=['plan','crates','nuget'],default='plan')
    parser.add_argument('--apply',action='store_true',help='Create missing policies; never replace or delete existing policies')
    parser.add_argument('--browser-executable')
    parser.add_argument('--report',type=Path,help='Save per-policy progress atomically, including partial/uncertain writes')
    args=parser.parse_args();manifest=load(args.manifest)
    if args.service=='plan':print(json.dumps(manifest,indent=2));return
    def progress(rows):
        document=dict(service=args.service,applied=args.apply,policies=rows)
        print(json.dumps(rows[-1]),flush=True)
        if args.report:
            args.report.parent.mkdir(parents=True,exist_ok=True)
            temporary=args.report.with_name(args.report.name+'.tmp')
            temporary.write_text(json.dumps(document,indent=2)+'\n',encoding='utf-8');temporary.replace(args.report)
    if args.service=='crates':
        token=os.environ.get(TOKEN_VARIABLE)
        if token and any(c.isspace() for c in token):raise ValueError('Invalid scoped configuration token')
        results=provision_crates(manifest,Crates(token),args.apply,progress)
    else:
        import asyncio
        results=asyncio.run(provision_nuget(manifest,args.apply,args.browser_executable,progress))
    print(json.dumps(dict(service=args.service,applied=args.apply,policies=results),indent=2))
    if any(r['status'] in ['AUTHENTICATION_REQUIRED','FIRST_PUBLICATION_REQUIRED','EXISTING_SCOPE_REVIEW_REQUIRED','EXISTING_IDENTITY_REVIEW_REQUIRED','CREATION_VERIFICATION_REQUIRED','CREATION_OUTCOME_UNKNOWN','LOOKUP_FAILED'] for r in results):sys.exit(2)

if __name__=='__main__':
    try:main()
    except (ValueError,urllib.error.URLError) as error:raise SystemExit(str(error))
