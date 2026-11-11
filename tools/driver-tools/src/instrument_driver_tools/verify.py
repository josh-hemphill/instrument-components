"""Build an independent driver's release-form packages and fresh native consumers."""
import hashlib
import json
import os
from pathlib import Path
import platform
import re
import shutil
import subprocess
import struct
import sys
import tarfile
import tomllib
import xml.etree.ElementTree as ET
import zipfile
from .feeds import RUST,DOTNET,VERSION,extract
from .profiles import validate,emit
from .cases import emit_cases

IGNORE=shutil.ignore_patterns('.git','target','bin','obj','artifacts','__pycache__')

def verify(repository,feeds,output,framework):
    repository=repository.resolve();feeds=feeds.resolve();output=output.resolve()
    if output==repository or repository in output.parents:raise ValueError('Proof output must be outside the driver checkout')
    if output.exists() and any(output.iterdir()):raise ValueError('Proof requires empty output')
    if platform.system() not in ['Linux','Windows'] or platform.machine().lower() not in ['x86_64','amd64']:raise ValueError('Native Windows/Linux x64 host required')
    settings=json.loads((repository/'driver-package.json').read_text(encoding='utf-8'))
    prepared=json.loads((feeds/'feeds.json').read_text(encoding='utf-8'))
    if prepared['mode']!='pinned-sdk-artifact-bootstrap':raise ValueError('Expected explicitly prepared bootstrap feeds')
    output.mkdir(parents=True,exist_ok=True);records=[]
    # Each driver proof has independent caches; copied feeds are immutable inputs.
    registry=output/'cargo-registry';shutil.copytree(prepared['cargoRegistry'],registry)
    cargo=output/'cargo-home';cargo.mkdir();(cargo/'config.toml').write_text('[source.crates-io]\nreplace-with="proof"\n[source.proof]\ndirectory='+json.dumps(str(registry))+'\n',encoding='utf-8')
    nuget=output/'nuget-feed';shutil.copytree(prepared['nugetFeed'],nuget)
    from xml.sax.saxutils import quoteattr
    config=output/'NuGet.Config';config.write_text('<configuration><packageSources><clear/><add key="proof" value='+quoteattr(str(nuget))+'/><add key="nuget" value="https://api.nuget.org/v3/index.json"/></packageSources><packageSourceMapping><packageSource key="proof"><package pattern="InstrumentComponents.*"/></packageSource><packageSource key="nuget"><package pattern="*"/></packageSource></packageSourceMapping></configuration>',encoding='utf-8')
    env=dict(os.environ,CARGO_HOME=str(cargo),CARGO_TARGET_DIR=str(output/'cargo-target'),NUGET_PACKAGES=str(output/'nuget-cache'),DOTNET_CLI_HOME=str(output/'dotnet-home'),DOTNET_NOLOGO='1',DOTNET_CLI_TELEMETRY_OPTOUT='1',PYTHONPATH='')
    def run(name,command,cwd=output,negative=False,native=False):
        print(name+': '+' '.join(map(str,command)),flush=True);log=output/(name+'.log')
        with log.open('w',encoding='utf-8') as stream:result=subprocess.run(list(map(str,command)),cwd=cwd,env=env,stdout=stream,stderr=subprocess.STDOUT)
        text=log.read_text(encoding='utf-8',errors='replace');records.append(dict(name=name,command=list(map(str,command)),exitCode=result.returncode,expectedFailure=negative,log=log.name,sha256=hashlib.sha256(log.read_bytes()).hexdigest()))
        (output/'execution.json').write_text(json.dumps(records,indent=2)+'\n',encoding='utf-8')
        failed=(result.returncode==0 or not re.search(r'\berror IL2026\b',text)) if negative else (result.returncode!=0 or native and re.search(r'\bwarning IL\d+\b',text,re.I))
        if failed:raise RuntimeError(name+' failed:\n'+text[-12000:])
        return text
    for source,outputs in [(repository/'profiles.json',emit),(repository/'cases.json',emit_cases)]:
        data=json.loads(source.read_text(encoding='utf-8'))
        if source.name=='profiles.json':validate(data)
        for name,content in outputs(data).items():
            if (repository/'generated'/name).read_text(encoding='utf-8')!=content:raise ValueError('Generated drift: '+name)
    stage=output/'source';shutil.copytree(repository,stage,ignore=IGNORE)
    manifest=tomllib.loads((stage/'Cargo.toml').read_text(encoding='utf-8'))
    if manifest['package']['name']!=settings['rustPackage'] or manifest['package']['version']!=VERSION:raise ValueError('Driver package identity mismatch')
    for name,value in manifest.get('dependencies',{}).items():
        if name not in {'instrument-api','instrument-scpi'} or value!='='+VERSION:raise ValueError('Unexpected production driver dependency')
    # Development locks contain bootstrap archive hashes, and are regenerated only
    # in this proof copy against the verified pinned artifact, never public feeds.
    for lock in stage.rglob('Cargo.lock'):lock.unlink()
    run('rust-package',['cargo','package','--offline','--allow-dirty','--no-verify'],stage)
    archive=output/'cargo-target/package'/(settings['rustPackage']+'-'+VERSION+'.crate');extract(archive,registry);crate=registry/(settings['rustPackage']+'-'+VERSION)
    packed=tomllib.loads((crate/'Cargo.toml').read_text(encoding='utf-8'))
    for value in packed.get('dependencies',{}).values():
        if value.get('path') or value.get('git'):raise ValueError('Path/source dependency in production archive')
    files={str(p.relative_to(crate)).replace('\\','/'):hashlib.sha256(p.read_bytes()).hexdigest() for p in crate.rglob('*') if p.is_file()}
    digest=hashlib.sha256(archive.read_bytes()).hexdigest();(crate/'.cargo-checksum.json').write_text(json.dumps(dict(files=files,package=digest)),encoding='utf-8')
    run('nuget-package',['dotnet','pack',stage/settings['dotnetProject'],'-c','Release','-o',nuget,'--configfile',config])
    nupkg=nuget/(settings['nugetPackage']+'.'+VERSION+'.nupkg')
    with zipfile.ZipFile(nupkg) as package:
        doc=ET.fromstring(package.read(next(n for n in package.namelist() if n.endswith('.nuspec'))));deps={d.attrib['id']:d.attrib['version'] for d in doc.findall('.//{*}dependency')}
        if deps!={'InstrumentComponents.Scpi':'['+VERSION+']'}:raise ValueError('Unexpected driver NuGet dependencies: '+str(deps))
        assemblies={Path(n).name for n in package.namelist() if n.endswith('.dll')}
        if assemblies!={settings['nugetPackage']+'.dll'}:raise ValueError('SDK implementation or extra DLL bundled in driver')
    # Generated-source proof and release-form package proof use the same immutable
    # golden corpus, independently from the production profile emitter.
    for mode in ['generated','packed']:
        consumer=output/('consumer-'+mode);shutil.copytree(stage,consumer,ignore=IGNORE)
        rust=consumer/'validation/rust';dotnet=consumer/'validation/dotnet';project=dotnet/'DriverProof.csproj'
        if mode=='generated':
            cargo_manifest=rust/'Cargo.toml';cargo_manifest.write_text(cargo_manifest.read_text(encoding='utf-8').replace(settings['rustPackage']+'="='+VERSION+'"',settings['rustPackage']+'={path="../.."}'),encoding='utf-8')
            project.write_text(project.read_text(encoding='utf-8').replace('<PackageReference Include="'+settings['nugetPackage']+'" Version="['+VERSION+']"/>','<ProjectReference Include="../../'+settings['dotnetProject']+'"/>'),encoding='utf-8')
        else:
            shutil.rmtree(consumer/'src');shutil.rmtree(consumer/'dotnet');(consumer/'generated/profiles.rs').unlink();(consumer/'generated/GeneratedProfiles.cs').unlink();(consumer/'Cargo.toml').unlink()
        for feature in ['', 'async']:
            flags=['--features',feature] if feature else []
            run(mode+'-rust-'+(feature or 'default'),['cargo','run','--offline','--release']+flags,rust)
            run(mode+'-rust-'+(feature or 'default')+'-clippy',['cargo','clippy','--offline','--release']+flags+['--','-D','warnings'],rust)
            metadata=json.loads(run(mode+'-rust-'+(feature or 'default')+'-metadata',['cargo','metadata','--offline','--format-version','1']+flags,rust))
            for package in metadata['packages']:
                if (package['name'] in RUST or mode=='packed' and package['name']==settings['rustPackage']) and not (package.get('source') or '').startswith('registry+'):
                    raise ValueError('Source SDK/driver reference leaked into package proof')
        (dotnet/'NuGet.Config').write_text(config.read_text(encoding='utf-8'),encoding='utf-8')
        project.write_text(project.read_text(encoding='utf-8').replace('net8.0',framework),encoding='utf-8')
        rid='win-x64' if os.name=='nt' else 'linux-x64';native_dir=output/(mode+'-native')
        publish=['dotnet','publish',project,'-c','Release','-r',rid,'--framework',framework,'--self-contained','true','--configfile',config,'-o',native_dir]
        run(mode+'-native-publish',publish,native=True)
        assets=json.loads((dotnet/'obj/project.assets.json').read_text(encoding='utf-8'))
        for name,library in assets['libraries'].items():
            if (name.split('/')[0] in DOTNET or mode=='packed' and name.split('/')[0]==settings['nugetPackage']) and library['type']!='package':raise ValueError('Source/project SDK/driver leaked into consumer')
        binary=native_dir/('DriverProof.exe' if os.name=='nt' else 'DriverProof')
        data=binary.read_bytes()
        if os.name=='nt':
            offset=struct.unpack_from('<I',data,0x3c)[0];optional=offset+24
            if data[offset:offset+4]!=b'PE\0\0' or struct.unpack_from('<H',data,offset+4)[0]!=0x8664 or struct.unpack_from('<H',data,optional)[0]!=0x20b or struct.unpack_from('<II',data,optional+112+14*8)!=(0,0):raise ValueError('Expected native x64 PE without CLR header')
        else:
            if data[:5]!=b'\x7fELF\x02' or struct.unpack_from('<H',data,18)[0]!=62:raise ValueError('Expected native x64 ELF')
            dependencies=run(mode+'-native-dependencies',['ldd',binary])
            if any(n in dependencies.lower() for n in ['coreclr','hostfxr','dotnet']):raise ValueError('Shared .NET runtime dependency')
        run(mode+'-native-execute',[binary])
        (output/(mode+'-native.json')).write_text(json.dumps(dict(rid=rid,framework=framework,sha256=hashlib.sha256(binary.read_bytes()).hexdigest(),executed=True)),encoding='utf-8')
        if mode=='packed':
            with (dotnet/'Program.cs').open('a',encoding='utf-8') as code:code.write('\ninternal static class WarningProbe { [System.Diagnostics.CodeAnalysis.RequiresUnreferencedCode("intentional diagnostic gate")] internal static void Trigger(){} internal static void Run()=>Trigger(); }\n')
            # Keep the annotated call reachable; the regular consumer completed above.
            code=dotnet/'Program.cs';code.write_text(code.read_text(encoding='utf-8').replace('await Proof.Run();','WarningProbe.Run(); await Proof.Run();'),encoding='utf-8')
            run('negative-native-warning-gate',publish,negative=True,native=True)
    inventory=[dict(package=settings['rustPackage'],file=archive.name,sha256=digest),dict(package=settings['nugetPackage'],file=nupkg.name,sha256=hashlib.sha256(nupkg.read_bytes()).hexdigest())]
    release=output/'packages';release.mkdir();shutil.copy2(archive,release);shutil.copy2(nupkg,release)
    (output/'packages.json').write_text(json.dumps(dict(mode=prepared['mode'],sdkPackages=prepared['packages'],driverPackages=inventory),indent=2)+'\n',encoding='utf-8')
    print('PASS independent driver '+settings['rustPackage']+' '+rid+'/'+framework+'; generated and packed, Rust default+async, four C# frontends, native warning gate; bootstrap feeds',flush=True)
