#!/usr/bin/env python3
"""Producer trust-boundary regressions; no network or publication."""
import copy
from pathlib import Path
import runpy
import unittest

VALIDATE = runpy.run_path(str(Path(__file__).with_name('recover-sdk-assets.py')))['validate_producer']

class ProducerTests(unittest.TestCase):
    def setUp(self):
        self.source='a'*40;self.tag='driver-sdk-v0.3.0-alpha.1';self.repo='example/instrument-components'
        self.run=dict(id=123,repository=dict(full_name=self.repo),event='push',path='.github/workflows/driver-sdk-preview.yml',head_sha=self.source,head_branch=self.tag,status='completed',conclusion='failure')
        names=['validate','crates','nuget','tool-assets']+['conformance / SDK '+os_name+' / '+tfm for os_name in ['ubuntu-latest','windows-latest'] for tfm in ['net8.0','net10.0']]
        self.jobs=[dict(name=n,status='completed',conclusion='success') for n in names]+[dict(name='release',status='completed',conclusion='failure')]
        self.artifacts=[dict(id=456,name='sdk-tool-assets',expired=False,digest='sha256:'+'b'*64,workflow_run=dict(id=123,head_sha=self.source))]
    def validate(self):
        return VALIDATE(self.run,self.jobs,self.artifacts,repository=self.repo,source=self.source,tag=self.tag)
    def test_only_asset_failure_is_recoverable(self):
        self.assertEqual(self.validate()['id'],456)
    def test_wrong_producer_identity_rejected(self):
        for key,value in [('event','pull_request'),('path','.github/workflows/other.yml'),('head_sha','c'*40),('head_branch','other-tag'),('status','in_progress')]:
            with self.subTest(key=key):
                original=self.run[key];self.run[key]=value
                with self.assertRaises(ValueError):self.validate()
                self.run[key]=original
        self.run['repository']['full_name']='other/repository'
        with self.assertRaises(ValueError):self.validate()
    def test_missing_or_failed_qualification_rejected(self):
        original=copy.deepcopy(self.jobs)
        for change in ['missing','duplicate','failure','skipped','running']:
            with self.subTest(change=change):
                self.jobs=copy.deepcopy(original)
                if change=='missing':self.jobs.pop(0)
                elif change=='duplicate':self.jobs[0]=copy.deepcopy(self.jobs[1])
                elif change=='running':self.jobs[0]['status']='in_progress'
                else:self.jobs[0]['conclusion']=change
                with self.assertRaises(ValueError):self.validate()
    def test_wrong_artifact_rejected(self):
        original=copy.deepcopy(self.artifacts)
        for change in ['missing','duplicate','expired','digest','source','run']:
            with self.subTest(change=change):
                self.artifacts=copy.deepcopy(original)
                if change=='missing':self.artifacts=[]
                elif change=='duplicate':self.artifacts*=2
                elif change=='expired':self.artifacts[0]['expired']=True
                elif change=='digest':self.artifacts[0]['digest']='sha256:bad'
                elif change=='source':self.artifacts[0]['workflow_run']['head_sha']='c'*40
                else:self.artifacts[0]['workflow_run']['id']=999
                with self.assertRaises(ValueError):self.validate()

if __name__=='__main__':unittest.main()
