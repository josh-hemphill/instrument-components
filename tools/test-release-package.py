"""Exercise registry-signature canonicalization without publishing anything."""
import io
from pathlib import Path
import runpy
import unittest
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

if __name__ == '__main__': unittest.main()
