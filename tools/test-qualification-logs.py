#!/usr/bin/env python3
"""Machine JSON, UTF-8 diagnostics and process failures retain independent evidence."""
import hashlib,json,os
from pathlib import Path
import subprocess,sys,tempfile,unittest
from qualification_logs import execute_logged

class QualificationLogsTests(unittest.TestCase):
    def test_structured_stdout_survives_network_diagnostics(self):
        with tempfile.TemporaryDirectory() as directory:
            out=Path(directory)
            command=[sys.executable,'-c','import sys;sys.stderr.buffer.write(b"Updating crates.io index\\n");sys.stdout.buffer.write("{\\"unit\\":\\"\\u00b5V\\"}".encode("utf-8"))']
            phase=execute_logged('metadata',command,cwd=out,env=dict(os.environ),output=out,structured_output=True)
            self.assertEqual(phase['exitCode'],0)
            self.assertEqual(json.loads((out/phase['log']).read_text(encoding='utf-8')),{'unit':'µV'})
            self.assertIn('Updating crates.io index',(out/phase['stderrLog']).read_text())
            for path,key in [('log','sha256'),('stderrLog','stderrSha256')]:self.assertEqual(hashlib.sha256((out/phase[path]).read_bytes()).hexdigest(),phase[key])
    def test_nonzero_exit_is_not_hidden_by_valid_json(self):
        with tempfile.TemporaryDirectory() as directory:
            out=Path(directory);p=execute_logged('failed',[sys.executable,'-c','import sys;print("{}");print("error",file=sys.stderr);sys.exit(7)'],cwd=out,env=dict(os.environ),output=out,structured_output=True)
            self.assertEqual(p['exitCode'],7);self.assertEqual(json.loads((out/p['log']).read_text()),{})
            self.assertIn('error',(out/p['stderrLog']).read_text())
    def test_human_log_keeps_both_streams(self):
        with tempfile.TemporaryDirectory() as directory:
            out=Path(directory);p=execute_logged('native',[sys.executable,'-c','import sys;print("result");print("diagnostic",file=sys.stderr)'],cwd=out,env=dict(os.environ),output=out)
            self.assertNotIn('stderrLog',p);text=(out/p['log']).read_text();self.assertIn('result',text);self.assertIn('diagnostic',text)

if __name__=='__main__':unittest.main()
