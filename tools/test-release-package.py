"""Exercise registry-signature canonicalization without publishing anything."""
import io
from pathlib import Path
import runpy
import unittest
from unittest import mock
import json
import subprocess
import tempfile
import zipfile
module = runpy.run_path(str(Path(__file__).with_name('release-package.py')))

def packed(entries):
    stream = io.BytesIO()
    with zipfile.ZipFile(stream, 'w') as archive:
        for name, content in entries:
            archive.writestr(name, content)
    return stream.getvalue()

class IdentityTests(unittest.TestCase):
    def test_registry_signature_and_member_order_do_not_change_payload(self):
        a = packed([('a.nuspec', 'source-sha'), ('lib/net8.0/a.dll', b'code')])
        b = packed([('.signature.p7s', b'signed'), ('lib/net8.0/a.dll', b'code'), ('a.nuspec', 'source-sha')])
        self.assertEqual(module['nuget_payload'](a), module['nuget_payload'](b))
    def test_code_or_source_identity_changes_are_rejected(self):
        a = packed([('a.nuspec', 'sha1'), ('lib/a.dll', 'code')])
        for b in [packed([('a.nuspec', 'sha2'), ('lib/a.dll', 'code')]), packed([('a.nuspec', 'sha1'), ('lib/a.dll', 'changed')])]:
            self.assertNotEqual(module['nuget_payload'](a), module['nuget_payload'](b))
    def test_duplicate_names_are_rejected(self):
        with self.assertRaisesRegex(ValueError, 'Duplicate'):
            module['nuget_payload'](packed([('a', 'one'), ('a', 'two')]))

class PublicationStateTests(unittest.TestCase):
    def invoke(self, root, remote, *, replay=None):
        archive=root/'Fixture.0.3.0-alpha.1.nupkg'
        archive.write_bytes(packed([('Fixture.nuspec','<package><metadata><id>Fixture</id><version>0.3.0-alpha.1</version></metadata></package>'),('lib/a.dll','code')]))
        args=['release-package.py','--nuget',str(archive),'--output',str(root/'proof')]
        if replay is not None:
            record=root/'retained.json';record.write_text(json.dumps(replay));args+=['--replay-record',str(record)]
        def git(command, **kwargs):
            if command[1:]==['rev-parse','HEAD']:return 'a'*40+'\n'
            if command[1:]==['status','--porcelain','--untracked-files=no']:return ''
            raise AssertionError(command)
        target=module['main'];self.calls=[]
        def run(command,**kwargs):
            self.calls.append(command)
            return subprocess.CompletedProcess(command,0)
        values=list(remote(archive.read_bytes()))
        with mock.patch('sys.argv',args),mock.patch.dict(target.__globals__,fetch=lambda url:values.pop(0)),mock.patch('subprocess.check_output',git),mock.patch('subprocess.run',run),mock.patch.dict('os.environ',{'NUGET_API_KEY':'never-record-this'}),mock.patch('time.sleep'),mock.patch.dict(target.__globals__,index_available=lambda *args:True):
            target()
        return json.loads((root/'proof/Fixture.json').read_text())

    def test_existing_equal_payload_skips_publish_and_records_identity(self):
        with tempfile.TemporaryDirectory() as folder:
            proof=self.invoke(Path(folder),lambda data:[data])
            self.assertEqual(self.calls,[])
            self.assertEqual(proof['stage'],'available-and-identity-verified')
            self.assertTrue(proof['indexVersionVerified'])
            self.assertEqual(proof['archiveSha256'],proof['remoteArchiveSha256'])

    def test_existing_different_payload_stops_without_publish(self):
        with tempfile.TemporaryDirectory() as folder:
            with self.assertRaisesRegex(ValueError,'payload mismatch'):
                self.invoke(Path(folder),lambda data:[packed([('Fixture.nuspec','other-source')])])
            self.assertEqual(self.calls,[])
            self.assertEqual(json.loads((Path(folder)/'proof/Fixture.json').read_text())['stage'],'identity-mismatch')

    def test_missing_publish_then_visible_archive_and_index(self):
        with tempfile.TemporaryDirectory() as folder:
            proof=self.invoke(Path(folder),lambda data:[None,None,data])
            self.assertEqual(len(self.calls),1)
            self.assertEqual(self.calls[0][:3],['dotnet','nuget','push'])
            self.assertNotIn('--skip-duplicate',self.calls[0])
            self.assertNotIn('never-record-this',json.dumps(proof))

    def test_replay_record_wrong_source_is_rejected_before_publication(self):
        with tempfile.TemporaryDirectory() as folder:
            with self.assertRaisesRegex(ValueError,'source/package mismatch'):
                self.invoke(Path(folder),lambda data:[],replay=dict(sourceCommit='b'*40,package='Fixture',version='0.3.0-alpha.1'))
            self.assertEqual(self.calls,[])

    def test_sparse_index_wait_yanked_and_checksum_mismatch(self):
        target=module['index_available'];row=dict(vers='0.3.0-alpha.1',yanked=False,cksum='good')
        with mock.patch.dict(target.__globals__,fetch=lambda url:None):self.assertFalse(target('fixture','0.3.0-alpha.1',True,'good'))
        for yanked in [False,True]:
            row['yanked']=yanked
            with mock.patch.dict(target.__globals__,fetch=lambda url:json.dumps(row).encode()):
                self.assertEqual(target('fixture','0.3.0-alpha.1',True,'good'),not yanked)
                if not yanked:
                    with self.assertRaisesRegex(ValueError,'index checksum'):target('fixture','0.3.0-alpha.1',True,'wrong')

if __name__ == '__main__': unittest.main()
