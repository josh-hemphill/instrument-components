"""Exercise a checked future preview through the built wheel and real feed path."""
import hashlib
import io
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tarfile
import tempfile
import unittest
import zipfile

ROOT = Path(__file__).resolve().parents[3]


class VersionTransitionTests(unittest.TestCase):
    def test_checked_future_version_builds_and_rejects_a_mixed_feed(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            shutil.copytree(ROOT / 'tools/driver-tools', root / 'tools/driver-tools', ignore=shutil.ignore_patterns('__pycache__', '*.egg-info', 'build', 'dist'))
            manifest = json.loads((ROOT / 'spec/driver-sdk-release.json').read_text())
            files = ['spec/driver-sdk-release.json', 'tools/validate-sdk-release.py', 'tools/validate-driver-sdk.py',
                     'crates/instrument-api/src/lib.rs', 'dotnet/src/InstrumentComponents.Abstractions/Drivers.cs']
            files += [f'crates/{name}/Cargo.toml' for name in manifest['rustPackages']]
            files += [f'dotnet/src/{name}/{name}.csproj' for name in manifest['nugetPackages']]
            for file in files:
                target = root / file
                target.parent.mkdir(parents=True, exist_ok=True)
                shutil.copyfile(ROOT / file, target)
            current = manifest['version']
            current_python = manifest['pythonVersion']
            future = '0.9.0-alpha.7'
            future_python = '0.9.0a7'
            # Change checked declarations, not runtime globals or assertions.
            for target in root.rglob('*'):
                if target.is_file() and target.suffix in ('.py', '.toml', '.json', '.csproj'):
                    target.write_text(target.read_text().replace(current, future).replace(current_python, future_python))
            release = root / 'spec/driver-sdk-release.json'
            candidate = json.loads(release.read_text())
            candidate.update(previewPublished=False, publicRegistryValidated=False)
            release.write_text(json.dumps(candidate))

            def run(command, **kwargs):
                result = subprocess.run(command, text=True, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, **kwargs)
                self.assertEqual(result.returncode, 0, result.stdout[-8000:])
                return result.stdout

            self.assertEqual(run([sys.executable, str(root / 'tools/validate-sdk-release.py'), '--tag', 'driver-sdk-v' + future]).strip(), future)
            wheel_dir = root / 'wheels'
            run([sys.executable, '-m', 'pip', 'wheel', '--no-deps', '-w', str(wheel_dir), str(root / 'tools/driver-tools')])
            wheel = wheel_dir / 'instrument_driver_tools-0.9.0a7-py3-none-any.whl'
            self.assertTrue(wheel.is_file())
            artifact = root / 'artifact'
            artifact.mkdir()
            records = []
            rust = ['instrument-api', 'instrument-runtime', 'instrument-scpi', 'instrument-testkit', 'instrument-components']
            dotnet = ['InstrumentComponents.Abstractions', 'InstrumentComponents.Runtime', 'InstrumentComponents.Scpi', 'InstrumentComponents.TestKit', 'InstrumentComponents']
            for name in rust + dotnet + ['instrument-driver-tools', 'cargo-dependencies']:
                if name in rust or name == 'cargo-dependencies':
                    filename = name + '-' + future + '.crate' if name in rust else name + '.tar.gz'
                    path = artifact / filename
                    with tarfile.open(path, 'w:gz') as archive:
                        crate = name + '-' + future if name in rust else 'ordinary-1.0.0'
                        data = ('[package]\nname="' + name + '"\nversion="' + future + '"\n').encode()
                        member = tarfile.TarInfo(crate + '/Cargo.toml')
                        member.size = len(data)
                        archive.addfile(member, io.BytesIO(data))
                elif name in dotnet:
                    path = artifact / (name + '.' + future + '.nupkg')
                    with zipfile.ZipFile(path, 'w') as archive:
                        archive.writestr(name + '.nuspec', '<package><metadata><id>' + name + '</id><version>' + future + '</version></metadata></package>')
                else:
                    path = artifact / wheel.name
                    shutil.copyfile(wheel, path)
                records.append(dict(package=name, archive='/producer/' + path.name, sha256=hashlib.sha256(path.read_bytes()).hexdigest()))
            (artifact / 'packages.json').write_text(json.dumps(records))
            env = dict(os.environ, PYTHONPATH=str(wheel))
            # Import the actual built wheel in a fresh process; no monkeypatching.
            probe = '''import json,runpy,sys,tomllib,xml.etree.ElementTree as ET
from pathlib import Path
from instrument_driver_tools import VERSION,python_version
from instrument_driver_tools.feeds import prepare
root=Path(sys.argv[1]);assert VERSION=='0.9.0-alpha.7';assert python_version(VERSION)=='0.9.0a7'
checks=runpy.run_path(str(root/'tools/validate-driver-sdk.py'));assert checks['SDK_VERSION']==VERSION
rust,dotnet=checks['core_only_manifests'](checks['SDK_VERSION'],'net10.0')
assert set(tomllib.loads(rust)['dependencies'].values())=={'='+VERSION}
assert {p.get('Version') for p in ET.fromstring(dotnet).findall('.//PackageReference')}=={'['+VERSION+']'}
result=prepare(root/'artifact',root/'feed');assert (Path(result['cargoRegistry'])/('instrument-api-'+VERSION)).is_dir()
assert (root/'feed/instrument_driver_tools-0.9.0a7-py3-none-any.whl').is_file()
print('PASS checked future preview, built wheel, real feeds and generated consumers')
'''
            run([sys.executable, '-c', probe, str(root)], env=env, cwd=root)
            # A valid transfer checksum cannot disguise an archive from alpha.1.
            row = next(r for r in records if r['package'] == 'instrument-api')
            old = artifact / 'instrument-api-0.2.0-alpha.1.crate'
            with tarfile.open(old, 'w:gz') as archive:
                data = b'[package]\nname="instrument-api"\nversion="0.2.0-alpha.1"\n'
                member = tarfile.TarInfo('instrument-api-0.2.0-alpha.1/Cargo.toml')
                member.size = len(data)
                archive.addfile(member, io.BytesIO(data))
            row.update(archive='/producer/' + old.name, sha256=hashlib.sha256(old.read_bytes()).hexdigest())
            (artifact / 'packages.json').write_text(json.dumps(records))
            negative = '''from pathlib import Path
import sys
from instrument_driver_tools.feeds import prepare
root=Path(sys.argv[1])
try: prepare(root/'artifact',root/'mixed-feed')
except ValueError as error:
    assert str(error)=='Unexpected crate version';assert not (root/'mixed-feed').exists()
else: raise AssertionError('Mixed preview feed accepted')
'''
            run([sys.executable, '-c', negative, str(root)], env=env, cwd=root)
