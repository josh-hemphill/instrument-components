"""Bootstrap inputs are never silently trusted or treated as published packages."""
import hashlib
import io
import json
from pathlib import Path
import sys
import tarfile
import tempfile
import unittest

sys.path.insert(0,str(Path(__file__).resolve().parents[1]/'src'))
from instrument_driver_tools.feeds import RUST,DOTNET,VERSION,extract,prepare

class FeedTests(unittest.TestCase):
    def fixture(self,root):
        artifact=root/'artifact';artifact.mkdir();records=[]
        for package in sorted(RUST|DOTNET|{'instrument-driver-tools','cargo-dependencies'}):
            name=package+'-'+VERSION+'.crate' if package in RUST else package+'.'+VERSION+'.nupkg' if package in DOTNET else 'cargo-dependencies.tar.gz' if package=='cargo-dependencies' else 'instrument_driver_tools-0.2.0a1-py3-none-any.whl'
            archive=artifact/name
            if package in RUST or package=='cargo-dependencies':
                with tarfile.open(archive,'w:gz') as tar:
                    member=tarfile.TarInfo((package+'-'+VERSION if package in RUST else 'ordinary-1.0.0')+'/Cargo.toml');data=b'[package]\n';member.size=len(data);tar.addfile(member,io.BytesIO(data))
            else:archive.write_bytes(b'transferred package')
            records.append(dict(package=package,archive='/producer/private/path/'+name,sha256=hashlib.sha256(archive.read_bytes()).hexdigest()))
        (artifact/'packages.json').write_text(json.dumps(records));return artifact
    def test_complete_feed_verifies_transfers_and_preserves_explicit_bootstrap_identity(self):
        with tempfile.TemporaryDirectory() as directory:
            root=Path(directory);artifact=self.fixture(root);result=prepare(artifact,root/'feed')
            self.assertEqual(result['mode'],'pinned-sdk-artifact-bootstrap')
            checksums=json.loads((Path(result['cargoRegistry'])/('instrument-api-'+VERSION)/'.cargo-checksum.json').read_text())
            self.assertIn('Cargo.toml',checksums['files']);self.assertEqual(checksums['package'],result['packages']['instrument-api'])
            self.assertIn('replace-with="bootstrap"',(Path(result['CARGO_HOME'])/'config.toml').read_text())
            with self.assertRaises(ValueError):prepare(artifact,root/'feed')
    def test_corruption_missing_and_duplicate_records_fail_before_feed_creation(self):
        for mutation in ['corrupt','missing','duplicate']:
            with self.subTest(mutation=mutation),tempfile.TemporaryDirectory() as directory:
                root=Path(directory);artifact=self.fixture(root);records=json.loads((artifact/'packages.json').read_text())
                if mutation=='corrupt':(artifact/Path(records[0]['archive']).name).write_bytes(b'changed')
                elif mutation=='missing':records.pop()
                else:records.append(records[0])
                (artifact/'packages.json').write_text(json.dumps(records))
                with self.assertRaises(ValueError):prepare(artifact,root/'feed')
                self.assertFalse((root/'feed').exists())
    def test_archive_traversal_and_links_are_rejected(self):
        for name,kind in [('../escape',tarfile.REGTYPE),('/absolute',tarfile.REGTYPE),('C:/escape',tarfile.REGTYPE),('link',tarfile.SYMTYPE)]:
            with self.subTest(name=name),tempfile.TemporaryDirectory() as directory:
                root=Path(directory);archive=root/'bad.tar'
                with tarfile.open(archive,'w') as tar:
                    member=tarfile.TarInfo(name);member.type=kind;tar.addfile(member)
                with self.assertRaises(ValueError):extract(archive,root/'feed')
