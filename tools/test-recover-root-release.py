#!/usr/bin/env python3
"""Root recovery source/job/artifact trust boundaries; no remote writes."""
import copy,json,runpy,tempfile,unittest,zipfile
from pathlib import Path
ROOT=Path(__file__).resolve().parents[1]
MODULE=runpy.run_path(str(ROOT/'tools/recover-root-release.py'))

class RootRecoveryTests(unittest.TestCase):
    def setUp(self):
        self.repo='example/instrument-components';self.source='a'*40;self.tag='v0.3.0-alpha.1'
        self.run=dict(id=123,repository=dict(full_name=self.repo),event='push',path='.github/workflows/release.yml',head_sha=self.source,head_branch=self.tag,status='completed')
        self.jobs=json.loads((ROOT/'validation/root-packages/recovery-producer-jobs.json').read_text())['jobs']
        names=['opentap-package','opentap-release-proof','driver-sdk-ubuntu-latest-net8.0','driver-sdk-ubuntu-latest-net10.0','driver-sdk-windows-latest-net8.0','driver-sdk-windows-latest-net10.0']
        self.artifacts=[dict(id=i,name=n,expired=False,digest='sha256:'+'b'*64,workflow_run=dict(id=123,head_sha=self.source)) for i,n in enumerate(names)]
    def validate(self):return MODULE['validate_producer'](self.run,self.jobs,self.artifacts,repository=self.repo,source=self.source,tag=self.tag)
    def test_actual_partial_failure_shape_is_recoverable(self):
        self.assertEqual(len(self.validate()),6)
    def test_each_required_job_must_pass(self):
        for job in self.jobs:
            if job['conclusion']!='success':continue
            with self.subTest(job=job['name']):
                job['conclusion']='failure'
                with self.assertRaises(ValueError):self.validate()
                job['conclusion']='success'
        self.jobs[0]=copy.deepcopy(self.jobs[1])
        with self.assertRaises(ValueError):self.validate()
    def test_wrong_source_event_and_repository_rejected(self):
        for key,value in [('head_sha','c'*40),('head_branch','different-tag'),('path','.github/workflows/driver-sdk-preview.yml'),('event','pull_request'),('status','in_progress')]:
            original=self.run[key];self.run[key]=value
            with self.subTest(key=key):
                with self.assertRaises(ValueError):self.validate()
            self.run[key]=original
        self.run['repository']['full_name']='different/repository'
        with self.assertRaises(ValueError):self.validate()
    def test_missing_ambiguous_or_wrong_artifact_rejected(self):
        original=copy.deepcopy(self.artifacts)
        for change in ['missing','duplicate','source','run','digest','expired']:
            self.artifacts=copy.deepcopy(original)
            if change=='missing':self.artifacts.pop()
            elif change=='duplicate':self.artifacts.append(copy.deepcopy(self.artifacts[0]))
            elif change=='source':self.artifacts[0]['workflow_run']['head_sha']='c'*40
            elif change=='run':self.artifacts[0]['workflow_run']['id']=999
            elif change=='digest':self.artifacts[0]['digest']='invalid'
            else:self.artifacts[0]['expired']=True
            with self.subTest(change=change):
                with self.assertRaises(ValueError):self.validate()
    def test_archive_path_escape_rejected_before_writes(self):
        with tempfile.TemporaryDirectory() as directory:
            base=Path(directory);archive=base/'bad.zip'
            with zipfile.ZipFile(archive,'w') as z:z.writestr('../escape','not a release artifact')
            with self.assertRaises(ValueError):MODULE['extract_checked'](archive,base/'output')
            self.assertFalse((base/'escape').exists())

if __name__=='__main__':unittest.main()
