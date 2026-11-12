"""Explicit, checksum-verified development feeds from a pinned SDK CI artifact.

This is bootstrap evidence, never proof of a public-registry release. CI must
obtain the artifact from the pinned producer; hashes detect transfer corruption.
"""
import hashlib
import json
from pathlib import Path, PurePosixPath
import shutil
import tarfile

RUST = {'instrument-api', 'instrument-runtime', 'instrument-scpi', 'instrument-testkit','instrument-components'}
DOTNET = {'InstrumentComponents.Abstractions', 'InstrumentComponents.Runtime', 'InstrumentComponents.Scpi', 'InstrumentComponents.TestKit','InstrumentComponents'}
from . import VERSION, python_version


def extract(archive, target):
    with tarfile.open(archive) as packed:
        members = packed.getmembers()
        if len(members)>100000 or sum(m.size for m in members)>1024**3:
            raise ValueError('Excessive feed archive')
        for member in members:
            parts=PurePosixPath(member.name).parts
            if not parts or '..' in parts or '\\' in member.name or ':' in member.name or member.name.startswith('/') or not (member.isfile() or member.isdir()):
                raise ValueError('Unsafe feed archive member')
        for member in members:
            destination=target.joinpath(*PurePosixPath(member.name).parts)
            if member.isdir():destination.mkdir(parents=True,exist_ok=True)
            else:
                destination.parent.mkdir(parents=True,exist_ok=True)
                with packed.extractfile(member) as source, destination.open('xb') as output:shutil.copyfileobj(source,output)


def prepare(artifacts, output):
    artifacts=artifacts.resolve();output=output.resolve()
    if output==artifacts or artifacts in output.parents or output in artifacts.parents:
        raise ValueError('Feeds must be separate from artifacts')
    if output.exists() and any(output.iterdir()):raise ValueError('Feeds require an empty directory')
    records=json.loads((artifacts/'packages.json').read_text(encoding='utf-8'))
    expected=RUST|DOTNET|{'instrument-driver-tools','cargo-dependencies'};selected={}
    for record in records:
        name=record['package']
        if name not in expected:continue
        if name in selected:raise ValueError('Duplicate feed package')
        basename=Path(record['archive'].replace('\\','/')).name
        candidates=[p for p in artifacts.rglob(basename) if p.is_file() and not p.is_symlink()]
        if len(candidates)!=1:raise ValueError('Missing or ambiguous feed archive: '+basename)
        archive=candidates[0]
        if hashlib.sha256(archive.read_bytes()).hexdigest()!=record['sha256']:raise ValueError('Feed checksum mismatch: '+basename)
        if name in RUST and basename!=name+'-'+VERSION+'.crate':raise ValueError('Unexpected crate version')
        if name in DOTNET and basename!=name+'.'+VERSION+'.nupkg':raise ValueError('Unexpected NuGet version')
        if name=='instrument-driver-tools' and basename!='instrument_driver_tools-'+python_version(VERSION)+'-py3-none-any.whl':raise ValueError('Unexpected tool wheel version')
        selected[name]=(archive,record)
    if set(selected)!=expected:raise ValueError('SDK feed package set incomplete')
    output.mkdir(parents=True,exist_ok=True);registry=output/'cargo-registry';registry.mkdir()
    extract(selected['cargo-dependencies'][0],registry)
    for name in sorted(RUST):
        archive,record=selected[name];extract(archive,registry);directory=registry/(name+'-'+VERSION)
        if not directory.is_dir():raise ValueError('Missing crate root')
        files={str(p.relative_to(directory)).replace('\\','/'):hashlib.sha256(p.read_bytes()).hexdigest() for p in directory.rglob('*') if p.is_file() and p.name!='.cargo-checksum.json'}
        (directory/'.cargo-checksum.json').write_text(json.dumps(dict(files=files,package=record['sha256'])),encoding='utf-8')
    cargo=output/'cargo-home';cargo.mkdir()
    (cargo/'config.toml').write_text('[source.crates-io]\nreplace-with="bootstrap"\n[source.bootstrap]\ndirectory='+json.dumps(str(registry))+'\n',encoding='utf-8')
    nuget=output/'nuget-feed';nuget.mkdir()
    for name in sorted(DOTNET):shutil.copy2(selected[name][0],nuget)
    # These are generated local paths, escaped as XML attributes.
    from xml.sax.saxutils import quoteattr
    config=output/'NuGet.Config'
    config.write_text('<configuration><packageSources><clear/><add key="bootstrap" value='+quoteattr(str(nuget))+'/><add key="nuget" value="https://api.nuget.org/v3/index.json"/></packageSources><packageSourceMapping><packageSource key="bootstrap"><package pattern="InstrumentComponents*"/></packageSource><packageSource key="nuget"><package pattern="*"/></packageSource></packageSourceMapping></configuration>',encoding='utf-8')
    wheel=output/selected['instrument-driver-tools'][0].name;shutil.copy2(selected['instrument-driver-tools'][0],wheel)
    result=dict(mode='pinned-sdk-artifact-bootstrap',CARGO_HOME=str(cargo),NUGET_PACKAGES=str(output/'nuget-cache'),nugetConfig=str(config),nugetFeed=str(nuget),cargoRegistry=str(registry),wheel=str(wheel),packages={name:record['sha256'] for name,(_,record) in selected.items()})
    (output/'feeds.json').write_text(json.dumps(result,indent=2)+'\n',encoding='utf-8')
    return result
