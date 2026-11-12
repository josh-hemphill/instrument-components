"""Offline policy tests: no registry writes, browser login, or real credentials."""
import copy
import importlib.util
import json
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import AsyncMock,patch
from types import SimpleNamespace

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
    def test_hidden_token_prompt_does_not_log_or_export_token(self):
        import contextlib,io
        output=io.StringIO()
        with patch.dict('os.environ',{},clear=True),patch.object(module.sys,'argv',['provision.py','--service','crates','--prompt-token']),patch.object(module.sys.stdin,'isatty',return_value=True),patch.object(module.getpass,'getpass',return_value='fixture-secret'),patch.object(module,'Crates') as client,patch.object(module,'provision_crates',return_value=[]),contextlib.redirect_stdout(output):
            module.main()
            client.assert_called_once_with('fixture-secret')
            self.assertNotIn(module.TOKEN_VARIABLE,module.os.environ)
        self.assertNotIn('fixture-secret',output.getvalue())
    def test_hidden_token_prompt_refuses_noninteractive_input(self):
        with patch.dict('os.environ',{},clear=True),patch.object(module.sys,'argv',['provision.py','--service','crates','--prompt-token']),patch.object(module.sys.stdin,'isatty',return_value=False),patch.object(module.getpass,'getpass') as prompt:
            with self.assertRaisesRegex(ValueError,'interactive terminal'):module.main()
            prompt.assert_not_called()
    def one(self):return {'githubOwnerId':46608115,'crates':[MANIFEST['crates'][0]]}
    def test_full_scope(self):
        self.assertEqual(len(MANIFEST['crates']),12);self.assertEqual(len(MANIFEST['nuget']),7)
        self.assertEqual(sum(len(p['packages']) for p in MANIFEST['nuget']),13)
    def test_nuget_error_diagnostics_do_not_echo_browser_data(self):
        reason='Selected NuGet owner requires new-package and version-push permissions'
        self.assertEqual(module.nuget_error_reason(Exception('Page.evaluate: Error: '+reason+' secret-fixture')),reason)
        self.assertEqual(module.nuget_error_reason(Exception('secret-fixture')),'Unexpected browser or account-page error')
        self.assertEqual(module.nuget_error_reason(Exception('TypeError: secret-fixture')),'Account-page data has unexpected field types')
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
    def test_created_crate_policy_requires_persisted_identity_without_retry(self):
        client=FakeCrates()
        def create_without_persisting(policy):
            client.created.append(policy)
            return 200,{'github_config':dict(policy,id=123,repository_owner_id=46608115)}
        client.create=create_without_persisting
        rows=module.provision_crates(self.one(),client,True)
        self.assertEqual(rows[0]['status'],'CREATION_VERIFICATION_REQUIRED')
        self.assertEqual(len(client.created),1)
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

class NuGetReadback(unittest.IsolatedAsyncioTestCase):
    async def test_created_policy_requires_matching_readback_without_retrying_write(self):
        for verification,expected in [
            ({'status':'ALREADY_CONFIGURED','policyId':123},'CREATED'),
            ({'status':'ALREADY_CONFIGURED','policyId':456},'CREATION_VERIFICATION_REQUIRED'),
            ({'status':'EXISTING_SCOPE_REVIEW_REQUIRED'},'CREATION_VERIFICATION_REQUIRED'),
            (RuntimeError('fixture readback failed'),'CREATION_VERIFICATION_REQUIRED'),
        ]:
            with self.subTest(verification=verification):
                created={'policy':'instrument-sdk-preview','status':'CREATED','policyId':123}
                page=SimpleNamespace(goto=AsyncMock(),wait_for_url=AsyncMock(),wait_for_function=AsyncMock(),reload=AsyncMock(),evaluate=AsyncMock(side_effect=[created,verification]))
                context=SimpleNamespace(new_page=AsyncMock(return_value=page),close=AsyncMock())
                launcher=SimpleNamespace(chromium=SimpleNamespace(launch_persistent_context=AsyncMock(return_value=context)))
                manager=AsyncMock();manager.__aenter__.return_value=launcher
                api=SimpleNamespace(async_playwright=lambda:manager)
                with patch.dict('sys.modules',{'playwright':SimpleNamespace(),'playwright.async_api':api}):
                    rows=await module.provision_nuget({'nuget':[MANIFEST['nuget'][0]]},True)
                self.assertEqual(rows[0]['status'],expected)
                self.assertEqual(rows[0].get('verifiedAfterReload',False),expected=='CREATED')
                self.assertEqual([call.args[1]['apply'] for call in page.evaluate.call_args_list],[True,False])
                context.close.assert_awaited_once()

class BootstrapSecrets(unittest.TestCase):
    def test_hidden_bootstrap_prompt_does_not_log_or_export_token(self):
        import contextlib,io
        helper=self.helper();output=io.StringIO()
        with patch.dict('os.environ',{},clear=True),patch.object(helper.sys.stdin,'isatty',return_value=True),patch.object(helper.getpass,'getpass',return_value='fixture-bootstrap-secret'),patch.object(helper,'run',return_value=[]) as run,contextlib.redirect_stdout(output):
            helper.main(['--apply','--prompt-token'])
            self.assertEqual(run.call_args.kwargs['token'],'fixture-bootstrap-secret')
            self.assertNotIn(helper.SECRET,helper.os.environ)
        self.assertNotIn('fixture-bootstrap-secret',output.getvalue())
    def test_hidden_bootstrap_prompt_requires_terminal(self):
        helper=self.helper()
        with patch.object(helper.sys.stdin,'isatty',return_value=False),patch.object(helper.getpass,'getpass') as prompt:
            with self.assertRaisesRegex(ValueError,'interactive terminal'):helper.main(['--apply','--prompt-token'])
            prompt.assert_not_called()
    @staticmethod
    def helper():
        spec=importlib.util.spec_from_file_location('bootstrap_secrets',ROOT/'bootstrap-secrets.py')
        helper=importlib.util.module_from_spec(spec);spec.loader.exec_module(helper);return helper
    def test_default_is_plan_without_github_calls(self):
        helper=self.helper()
        def forbidden(*args,**kwargs):raise AssertionError('Plan contacted GitHub')
        rows=helper.run(MANIFEST,command=forbidden);self.assertEqual(len(rows),6)
        self.assertTrue(all(row['status']=='WOULD_CREATE_IF_MISSING' for row in rows))
    def test_token_only_on_stdin_and_existing_preserved(self):
        helper=self.helper();calls=[]
        def command(args,**kwargs):
            calls.append((args,kwargs))
            if args[2]=='list':return SimpleNamespace(returncode=0,stdout='[{"name":"CARGO_BOOTSTRAP_TOKEN"}]' if args[4].endswith('keysight') else '[]')
            self.assertEqual(kwargs['input'],'fixture-token-only');self.assertNotIn('fixture-token-only',str(args));return SimpleNamespace(returncode=0)
        with patch.dict('os.environ',{'CARGO_BOOTSTRAP_TOKEN':'fixture-token-only'}):rows=helper.run(MANIFEST,True,command=command)
        self.assertEqual(sum(row['status']=='CREATED' for row in rows),5);self.assertEqual(sum(row['status']=='EXISTING_SECRET_PRESERVED' for row in rows),1)
        self.assertEqual(sum(args[2]=='set' for args,_ in calls),5)

if __name__=='__main__':unittest.main()
