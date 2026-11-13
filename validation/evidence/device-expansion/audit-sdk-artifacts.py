"""Inspect actual producer ZIPs without loading or trusting their executable code."""
import argparse,hashlib,io,json,re,struct,tarfile,tomllib,zipfile
from pathlib import Path, PurePosixPath
import xml.etree.ElementTree as ET
parser=argparse.ArgumentParser();parser.add_argument('directory',type=Path);parser.add_argument('--core-repository',type=Path,required=True);args=parser.parse_args();root=args.directory
metadata=json.loads((root/'github-artifacts.json').read_text())['artifacts'];records=[]
sha=lambda b:hashlib.sha256(b).hexdigest()
rust={'instrument-api':set(),'instrument-runtime':{'instrument-api','tokio'},'instrument-scpi':{'instrument-api'},'instrument-testkit':{'instrument-api','instrument-scpi'},'instrument-components':{'instrument-api','instrument-runtime'}}
dotnet={'InstrumentComponents':{'InstrumentComponents.Runtime'},'InstrumentComponents.Abstractions':set(),'InstrumentComponents.Runtime':{'InstrumentComponents.Abstractions'},'InstrumentComponents.Scpi':{'InstrumentComponents.Abstractions'},'InstrumentComponents.TestKit':{'InstrumentComponents.Scpi'}}
for artifact in [a for a in metadata if a['name'].startswith('driver-sdk-')]:
    data=(root/(artifact['name']+'.zip')).read_bytes();assert artifact['digest']=='sha256:'+sha(data)
    with zipfile.ZipFile(io.BytesIO(data)) as z:
        names=z.namelist();validation=json.loads(z.read('validation.json'))
        for phase in validation:
            # Logs contain platform-specific absolute paths; only the final basename is archived.
            basename=phase['log'].replace('\\','/').split('/')[-1];log=z.read(basename)
            assert sha(log)==phase['sha256'];assert phase['exitCode']==(1 if phase['expectedFailure'] else 0)
            if phase['name']=='negative-trimming-gate':
                text=log.decode(errors='replace');assert re.search(r'\berror IL2026\b',text) and re.search(r'\berror IL3050\b',text)
        platform=json.loads(z.read('platform.json'));assert platform['nativeExecuted']
        binary=z.read('native/AotConsumer'+('.exe' if platform['rid']=='win-x64' else ''));assert sha(binary)==platform['sha256']
        if platform['rid']=='win-x64':
            assert binary[:2]==b'MZ';offset=struct.unpack_from('<I',binary,0x3c)[0];assert binary[offset:offset+4]==b'PE\0\0' and struct.unpack_from('<H',binary,offset+4)[0]==0x8664
            assert struct.unpack_from('<II',binary,offset+24+112+14*8)==(0,0)
        else:
            assert binary[:5]==b'\x7fELF\x02' and struct.unpack_from('<H',binary,18)[0]==62 and platform['isolationExecuted']
            assert not any(k in z.read('native-dependencies.log').decode().lower() for k in ['coreclr','hostfxr','dotnet'])
        assert 'eleven operations, sync/async request and unit contracts' in z.read('native-aot-behavior.log').decode()
        inventory=json.loads(z.read('packages.json'));package_records=[]
        for package in inventory:
            basename=package['archive'].replace('\\','/').split('/')[-1]
            matches=[n for n in names if n.endswith('/'+basename) or n==basename];assert len(matches)==1
            body=z.read(matches[0]);assert sha(body)==package['sha256']
            name=package['package']
            if name in rust:
                with tarfile.open(fileobj=io.BytesIO(body)) as crate:
                    members=crate.getnames();cargo=tomllib.loads(crate.extractfile(next(n for n in members if n.endswith('/Cargo.toml'))).read().decode())
                    assert set(cargo.get('dependencies',{}))==rust[name]
                    assert all(not any(k in d for k in ['path','git']) for d in cargo.get('dependencies',{}).values())
                    assert not any('/hp5334' in n or '/hp3497' in n or '/hp_radio' in n or '/qualified_scpi' in n or '/hp34980' in n or '/hp3585' in n or '/tds1002' in n or 'instrument-driver-keysight' in n for n in members)
                    if name=='instrument-api':
                        actual=crate.extractfile(next(n for n in members if n.endswith('/src/catalog_generated.rs'))).read().replace(b'\r\n',b'\n')
                        expected=(args.core_repository/'crates/instrument-api/src/catalog_generated.rs').read_bytes().replace(b'\r\n',b'\n');assert actual==expected
                package_records.append({'package':name,'sha256':sha(body),'productionDependencies':sorted(rust[name]),'members':members})
            elif name in dotnet:
                with zipfile.ZipFile(io.BytesIO(body)) as nuget:
                    members=nuget.namelist();doc=ET.fromstring(nuget.read(next(n for n in members if n.endswith('.nuspec'))))
                    deps={d.attrib['id']:d.attrib['version'] for d in doc.findall('.//{*}dependency')};assert set(deps)==dotnet[name] and all(v=='[0.2.0-alpha.1]' for v in deps.values())
                    assert {Path(n).name for n in members if n.endswith('.dll')}=={name+'.dll'}
                package_records.append({'package':name,'sha256':sha(body),'productionDependencies':deps,'members':members})
        assert {p['package'] for p in package_records}==set(rust)|set(dotnet)
        records.append({'artifact':{k:artifact[k] for k in ['id','name','digest','size_in_bytes']},'verifiedZipSha256':sha(data),'platform':platform,'nativeFormatVerified':True,'phaseCount':len(validation),'packages':package_records})
    print('PASS',artifact['name'],'producer binary/log hashes/typed operations/core package separation')
assert len(records)==4
(root/'independent-sdk-audit.json').write_text(json.dumps({'schemaVersion':1,'artifacts':records},indent=2)+'\n')
