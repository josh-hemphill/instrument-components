#!/usr/bin/env python3
"""Real-Git regression tests for tag checkout shapes; no GitHub writes."""
import os
from pathlib import Path
import runpy
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

HELPER = Path(__file__).with_name('release-assets.py')

class TagFetchTests(unittest.TestCase):
    def test_remote_tag_identity_without_local_replacement(self):
        real_run = subprocess.run
        with tempfile.TemporaryDirectory() as temp:
            base = Path(temp)
            def git(*args, cwd=None):
                return subprocess.check_output(['git', *map(str,args)], cwd=cwd or base, text=True, stderr=subprocess.PIPE).strip()
            remote=base/'remote.git';seed=base/'seed'
            git('init','--bare',remote);git('init',seed)
            git('config','user.name','Release regression',cwd=seed)
            git('config','user.email','release-test@example.invalid',cwd=seed)
            (seed/'README').write_text('first\n')
            git('add','README',cwd=seed);git('commit','-m','first',cwd=seed)
            first=git('rev-parse','HEAD',cwd=seed)
            git('tag','-a','driver-sdk-vtest','-m','annotated release',cwd=seed)
            git('push',remote,'HEAD:refs/heads/latest','refs/tags/driver-sdk-vtest',cwd=seed)
            git('symbolic-ref','HEAD','refs/heads/latest',cwd=remote)
            (seed/'README').write_text('second\n')
            git('add','README',cwd=seed);git('commit','-m','second',cwd=seed)
            git('tag','-a','driver-sdk-vwrong','-m','different source',cwd=seed)
            git('push',remote,'refs/tags/driver-sdk-vwrong',cwd=seed)
            assets=base/'assets';assets.mkdir();(assets/'example.whl').write_bytes(b'original asset bytes')
            for shape in ['absent','lightweight','annotated','wrong-remote-source']:
                with self.subTest(shape=shape):
                    checkout=base/shape;git('clone','--no-tags',remote,checkout)
                    tag='driver-sdk-vwrong' if shape=='wrong-remote-source' else 'driver-sdk-vtest'
                    if shape=='lightweight':git('tag',tag,first,cwd=checkout)
                    if shape=='annotated':git('fetch','origin','refs/tags/'+tag+':refs/tags/'+tag,cwd=checkout)
                    before=git('tag','--list','--format=%(refname) %(objectname)',cwd=checkout)
                    remote_before=git('show-ref','--tags',cwd=remote)
                    calls=[]
                    def command(args,**kwargs):
                        if args[0]!='gh':return real_run(args,**kwargs)
                        calls.append(args)
                        code=0;stdout='';stderr=''
                        if args[1:3]==['release','view']:code=1
                        elif args[1:3]==['repo','view']:stdout='example/instruments\n'
                        elif args[1]=='api':code=1;stderr='HTTP 404'
                        elif args[1:3] not in [['release','create'],['release','upload']]:raise AssertionError(args)
                        return subprocess.CompletedProcess(args,code,stdout,stderr)
                    old=Path.cwd()
                    try:
                        os.chdir(checkout)
                        with patch.object(subprocess,'run',command),patch.object(sys,'argv',[str(HELPER),'--tag',tag,'--assets',str(assets)]):
                            if shape=='wrong-remote-source':
                                with self.assertRaisesRegex(ValueError,'immutable tag'):runpy.run_path(str(HELPER),run_name='__main__')
                            else:runpy.run_path(str(HELPER),run_name='__main__')
                    finally:os.chdir(old)
                    self.assertEqual(before,git('tag','--list','--format=%(refname) %(objectname)',cwd=checkout))
                    self.assertEqual(remote_before,git('show-ref','--tags',cwd=remote))
                    self.assertEqual(first,git('rev-parse','HEAD',cwd=checkout))
                    if shape=='wrong-remote-source':self.assertEqual(calls,[])
                    else:
                        self.assertTrue(any(c[1:3]==['release','create'] for c in calls))
                        self.assertTrue(any(c[1:3]==['release','upload'] for c in calls))
                        self.assertTrue((checkout/'release-assets-proof.json').is_file())

if __name__=='__main__':unittest.main()
