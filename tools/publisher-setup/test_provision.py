"""Offline policy tests: no registry writes, browser login, or real credentials."""
import copy
import importlib.util
import json
from pathlib import Path
import subprocess
import tempfile
import unittest

ROOT=Path(__file__).resolve().parent
spec=importlib.util.spec_from_file_location('provision',ROOT/'provision.py')
module=importlib.util.module_from_spec(spec);spec.loader.exec_module(module)
MANIFEST=module.load(ROOT.parents[1]/'spec/publishers.json')

class FakeCrates:
    def __init__(self,exists=True,status=200,rows=(),token='test-only'):
        self.found=exists;self.status=status;self.rows=list(rows);self.token=token;self.created=[]
    def exists(self,name):return self.found
    def policies(self,name):return self.status,self.rows
    def create(self,policy):
        self.created.append(policy);self.rows.append(dict(policy,id=123,repository_owner_id=46608115))
        return 200,{'github_config':self.rows[-1]}

class Policies(unittest.TestCase):
    def one(self):return {'githubOwnerId':46608115,'crates':[MANIFEST['crates'][0]]}
    def test_full_scope(self):
        self.assertEqual(len(MANIFEST['crates']),12);self.assertEqual(len(MANIFEST['nuget']),7)
        self.assertEqual(sum(len(p['packages']) for p in MANIFEST['nuget']),13)
    def test_dry_run_and_apply(self):
        client=FakeCrates();self.assertEqual(module.provision_crates(self.one(),client)[0]['status'],'WOULD_CREATE');self.assertEqual(client.created,[])
        self.assertEqual(module.provision_crates(self.one(),client,True)[0]['status'],'CREATED')
        self.assertEqual(module.provision_crates(self.one(),client,True)[0]['status'],'ALREADY_CONFIGURED');self.assertEqual(len(client.created),1)
    def test_post_write_malformed_and_interruption(self):
        for response in [None,[],{'github_config':[]},{'github_config':None}]:
            client=FakeCrates();client.create=lambda policy:(200,response);progress=[]
            result=module.provision_crates(self.one(),client,True,lambda rows:progress.append(copy.deepcopy(rows)))
            self.assertEqual(result[-1]['status'],'CREATION_VERIFICATION_REQUIRED');self.assertEqual(len(progress),1)
        client=FakeCrates()
        def interrupted(policy):raise OSError('interrupted')
        client.create=interrupted;progress=[]
        with self.assertRaises(ValueError):module.provision_crates(self.one(),client,True,lambda rows:progress.append(copy.deepcopy(rows)))
        self.assertEqual(progress[-1][-1]['status'],'CREATION_OUTCOME_UNKNOWN')
    def test_stale_owner_and_progress(self):
        policy=self.one()['crates'][0];client=FakeCrates(rows=[dict(policy,repository_owner_id=1)]);progress=[]
        result=module.provision_crates(self.one(),client,True,lambda rows:progress.append(copy.deepcopy(rows)))
        self.assertEqual(result[0]['status'],'EXISTING_IDENTITY_REVIEW_REQUIRED');self.assertEqual(client.created,[]);self.assertEqual(len(progress),1)
    def test_missing_crate_and_auth_never_write(self):
        for client,expected in [(FakeCrates(exists=False),'FIRST_PUBLICATION_REQUIRED'),(FakeCrates(status=403),'AUTHENTICATION_REQUIRED'),(FakeCrates(token=None),'AUTHENTICATION_REQUIRED')]:
            self.assertEqual(module.provision_crates(self.one(),client,True)[0]['status'],expected);self.assertEqual(client.created,[])
    def test_different_existing_policy_preserved(self):
        old=dict(self.one()['crates'][0],workflow_filename='old.yml');client=FakeCrates(rows=[old])
        module.provision_crates(self.one(),client,True);self.assertEqual(client.rows[0],old)
    def test_exact_crate_tuple(self):
        policy=self.one()['crates'][0]
        self.assertTrue(module.same_crate_policy(dict(policy,repository_owner=policy['repository_owner'].upper()),policy))
        for field in ['crate','repository_name','workflow_filename','environment']:
            self.assertFalse(module.same_crate_policy(dict(policy,**{field:'different'}),policy))
    def test_manifest_rejects_wildcard_and_bad_ids(self):
        for mutate in [lambda p:p['nuget'][0]['packages'].append('*'),lambda p:p['nuget'][0].update(repositoryId=True),lambda p:p['crates'][0].update(workflow_filename='../release.yml')]:
            data=copy.deepcopy(MANIFEST);mutate(data)
            with tempfile.TemporaryDirectory() as directory:
                path=Path(directory)/'policies.json';path.write_text(json.dumps(data))
                with self.assertRaises(ValueError):module.load(path)
    def test_lookup_errors_not_missing(self):
        client=module.Crates();client.request=lambda path:(500,None)
        with self.assertRaises(ValueError):client.exists('test')
    def test_pagination_preserves_crate_and_refuses_redirect(self):
        client=module.Crates();calls=[]
        def request(path):
            calls.append(path)
            return 200,dict(github_configs=[],meta={'next_page':'?seek=next' if len(calls)==1 else None})
        client.request=request;self.assertEqual(client.policies('test-crate'),(200,[]));self.assertIn('crate=test-crate',calls[1])
        with self.assertRaises(ValueError):module.NoRedirect().redirect_request(None,None,None,None,None,None)
    def test_nuget_same_origin_and_exact_payload(self):
        # Node's VM stands in for the account-page JavaScript globals. Fixtures
        # exercise production code; no registry endpoint is contacted.
        result=subprocess.run(['node',ROOT/'test-nuget.cjs'],capture_output=True,text=True,check=True)
        self.assertIn('PASS',result.stdout)

if __name__=='__main__':unittest.main()
