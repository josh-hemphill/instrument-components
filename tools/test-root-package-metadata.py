#!/usr/bin/env python3
"""Cargo JSON stdout remains parseable while all diagnostics are retained."""
import hashlib,json,os
from pathlib import Path
import runpy,subprocess,sys,tempfile,unittest

ROOT=Path(__file__).resolve().parents[1]
sys.path.insert(0,str(ROOT/'tools/driver-tools/src'))
MODULE=runpy.run_path(str(ROOT/'tools/validate-root-packages.py'))
CAPTURE=MODULE['execute_logged']

class MetadataTests(unittest.TestCase):
    def test_output_protects_checkouts_and_preserves_sdk_sidecar(self):
        with tempfile.TemporaryDirectory() as directory:
            base=Path(directory);source=base/'payload';helper=base/'helper';sdk=base/'sdk'
            for root in [source,helper]:
                for output in [root,root/'output',base]:
                    with self.subTest(root=root,output=output):
                        with self.assertRaises(ValueError):MODULE['validate_output_location'](output,source,helper,sdk)
            MODULE['validate_output_location'](base/'separate',source,helper,sdk)
            for output in [sdk,base]:
                with self.assertRaises(ValueError):MODULE['validate_output_location'](output,source,helper,sdk)
            MODULE['validate_output_location'](sdk/'root-packages',source,helper,sdk)
    def test_network_diagnostics_do_not_corrupt_json(self):
        with tempfile.TemporaryDirectory() as directory:
            out=Path(directory)
            command=[sys.executable,'-c','import sys,json;print("Updating crates.io index",file=sys.stderr,flush=True);print(json.dumps({"packages":[]}))']
            with (out/'old.log').open('w') as stream:subprocess.run(command,stdout=stream,stderr=subprocess.STDOUT,check=True)
            with self.assertRaises(json.JSONDecodeError):json.loads((out/'old.log').read_text())
            phase=CAPTURE('metadata',command,cwd=out,env=dict(os.environ),output=out,structured_output=True)
            self.assertEqual(phase['exitCode'],0)
            self.assertEqual(json.loads((out/phase['log']).read_text()),{'packages':[]})
            self.assertIn('Updating crates.io index',(out/phase['stderrLog']).read_text())
            for path,key in [('log','sha256'),('stderrLog','stderrSha256')]:self.assertEqual(hashlib.sha256((out/phase[path]).read_bytes()).hexdigest(),phase[key])
    def test_nonzero_status_and_both_streams_retained(self):
        with tempfile.TemporaryDirectory() as directory:
            out=Path(directory)
            phase=CAPTURE('failed',[sys.executable,'-c','import sys;print("{}");print("failed metadata",file=sys.stderr);sys.exit(7)'],cwd=out,env=dict(os.environ),output=out,structured_output=True)
            self.assertEqual(phase['exitCode'],7)
            self.assertEqual(json.loads((out/phase['log']).read_text()),{})
            self.assertIn('failed metadata',(out/phase['stderrLog']).read_text())

if __name__=='__main__':unittest.main()
