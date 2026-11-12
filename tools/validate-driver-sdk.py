#!/usr/bin/env python3
"""Build packed SDK/TestKit and installed-tool consumers outside the checkout.

Runs on native Linux x64 and Windows x64; no publication, device repos or hardware.
Linux optionally isolates the executable from source/network/shared .NET runtimes.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import platform
import re
import shutil
import struct
import subprocess
import sys
import tarfile
import tempfile
import tomllib
import xml.etree.ElementTree as ET
import zipfile

ROOT = Path(__file__).resolve().parent.parent
SDK_RELEASE = json.loads((ROOT/'spec/driver-sdk-release.json').read_text())
SDK_VERSION = SDK_RELEASE['version']
PYTHON_VERSION = SDK_RELEASE['pythonVersion']
IGNORE = shutil.ignore_patterns('target', 'bin', 'obj', '__pycache__', '.git', 'build', 'dist', '*.egg-info')


def check_rust_pins(manifest):
    version=manifest['package']['version']
    for name,settings in manifest.get('dependencies',{}).items():
        requirement=settings.get('version') if isinstance(settings,dict) else settings
        if name.startswith('instrument-') and requirement!='='+version:
            raise ValueError('Packed SDK dependency is not exact: '+name)


def check_nuget_pins(ranges, version=None):
    version = SDK_VERSION if version is None else version
    if any(v!='['+version+']' for v in ranges.values()):
        raise ValueError('NuGet SDK dependency is not exact: '+str(ranges))


def core_only_manifests(version, framework):
    return ('[package]\nname="core-only"\nversion="0.0.0"\nedition="2021"\n[workspace]\n[dependencies]\ninstrument-runtime="=@SDK_VERSION@"\ninstrument-scpi="=@SDK_VERSION@"\n[features]\nasync=["instrument-runtime/async","instrument-scpi/async"]\n'.replace("@SDK_VERSION@", version),
            '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>'+framework+'</TargetFramework></PropertyGroup><ItemGroup><PackageReference Include="InstrumentComponents.Runtime" Version="[@SDK_VERSION@]"/><PackageReference Include="InstrumentComponents.Scpi" Version="[@SDK_VERSION@]"/></ItemGroup></Project>'.replace("@SDK_VERSION@", version))


def main():
    # Validate the exact declarations used below before building any artifacts.
    import runpy
    runpy.run_path(str(ROOT/'tools/validate-sdk-release.py'))['validate'](ROOT)
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--artifacts', type=Path)
    parser.add_argument('--framework', choices=['net8.0', 'net10.0'], default='net8.0')
    parser.add_argument('--cpp-compiler')
    parser.add_argument('--isolate-runtime', action='store_true')
    parser.add_argument('--isolation-sudo', action='store_true', help='Run only Linux bubblewrap through sudo (hosted CI namespace policy)')
    args = parser.parse_args()
    windows = platform.system() == 'Windows'
    if platform.machine().lower() not in ['x86_64', 'amd64'] or platform.system() not in ['Windows', 'Linux']:
        parser.error('Native x64 Linux or Windows required; cross-publication is not execution evidence')
    if args.isolate_runtime and windows:
        parser.error('--isolate-runtime uses Linux bubblewrap; Windows verifies native PE and executes natively')
    if args.isolation_sudo and (windows or not args.isolate_runtime):
        parser.error('--isolation-sudo requires Linux --isolate-runtime')
    rid = 'win-x64' if windows else 'linux-x64'
    work = args.artifacts.resolve() if args.artifacts else Path(tempfile.mkdtemp(prefix='instrument-sdk-'))
    if work == ROOT or ROOT in work.parents:
        parser.error('Artifact directory must be outside the checkout')
    work.mkdir(parents=True, exist_ok=True)
    if any(work.iterdir()):
        parser.error('Artifact directory must be empty; fresh caches are part of the proof')
    env = dict(os.environ, DOTNET_CLI_HOME=str(work/'dotnet-home'), DOTNET_NOLOGO='1', DOTNET_CLI_TELEMETRY_OPTOUT='1')
    records = []

    def run(name, command, cwd=work, extra=None, negative=False, diagnostics=False):
        log = work/(name+'.log'); command = list(map(str, command))
        print(name+': '+' '.join(command), flush=True)
        with log.open('w') as output:
            result = subprocess.run(command, cwd=cwd, env=dict(env, **(extra or {})), stdout=output, stderr=subprocess.STDOUT)
        text = log.read_text(errors='replace')
        records.append(dict(name=name, command=command, cwd=str(cwd), exitCode=result.returncode, expectedFailure=negative, log=str(log), sha256=hashlib.sha256(log.read_bytes()).hexdigest()))
        (work/'validation.json').write_text(json.dumps(records, indent=2)+'\n')
        if negative:
            if result.returncode == 0 or any(not re.search(r'\berror '+diagnostic+r'\b',text) for diagnostic in ['IL2026','IL3050']):
                raise RuntimeError('Diagnostic gate did not fail with actual IL2026 and IL3050: '+str(log))
        elif result.returncode or diagnostics and re.search(r'\bwarning IL\d+\b', text, re.I):
            print(text[-12000:], flush=True); raise RuntimeError(f'Failed check (exit {result.returncode}): {log}')
        return text

    run('toolchain-rust', ['rustc', '-Vv']); run('toolchain-dotnet', ['dotnet', '--info'])
    # Package/install the CLI into a clean venv with offline dependencies.
    python_feed = work/'python-feed'; python_feed.mkdir()
    tool_source=work/'tool-source';shutil.copytree(ROOT/'tools/driver-tools',tool_source,ignore=IGNORE)
    run('tool-wheel', [sys.executable, '-m', 'pip', 'wheel', '--no-deps', '-w', python_feed, tool_source])
    run('tool-dependency-wheels', [sys.executable, '-m', 'pip', 'download', '--index-url', 'https://pypi.org/simple', '--dest', python_feed]+tomllib.loads((tool_source/'pyproject.toml').read_text())['project']['dependencies'])
    run('tool-fresh-venv', [sys.executable, '-m', 'venv', work/'tool-venv'])
    python = work/'tool-venv'/('Scripts/python.exe' if windows else 'bin/python')
    run('tool-install-offline', [python, '-m', 'pip', 'install', '--no-index', '--find-links', python_feed, 'instrument-driver-tools=='+PYTHON_VERSION])
    # Never inherit PYTHONPATH pointing to the checkout.
    tool_env = dict(PYTHONPATH='')
    for name, inp in [('profiles', ROOT/'validation/scpi/profiles.json'), ('cases', ROOT/'spec/driver-scpi-cases.json')]:
        shutil.copy2(inp, work/(name+'.json'))
    generated = work/'generated'
    generate = [python, '-m', 'instrument_driver_tools.cli', 'generate', '--input', work/'profiles.json', '--output-dir', generated, '--contract-major', '1']
    generate_cases = [python, '-m', 'instrument_driver_tools.cli', 'generate-tests', '--input', work/'cases.json', '--output-dir', generated]
    run('installed-tool-generate', generate, extra=tool_env)
    run('installed-tool-generate-check', generate+['--check'], extra=tool_env)
    run('installed-tool-generate-cases', generate_cases, extra=tool_env)
    run('installed-tool-generate-cases-check', generate_cases+['--check'], extra=tool_env)

    for name,inp in [('choice-profiles',ROOT/'validation/scpi/choice-profiles.json'),('choice-cases',ROOT/'spec/driver-choice-cases.json')]:shutil.copy2(inp,work/(name+'.json'))
    choice_generated=work/'choice-generated'
    choice_commands=[('profiles','generate','choice-profiles','choice_profiles'),('cases','generate-tests','choice-cases','choice_cases')]
    for label,subcommand,source,module in choice_commands:
        command=[python,'-m','instrument_driver_tools.cli',subcommand,'--input',work/(source+'.json'),'--output-dir',choice_generated,'--module',module,'--namespace','GeneratedChoice']
        if subcommand=='generate':command+=['--contract-major','1']
        run('installed-choice-'+label,command,extra=tool_env);run('installed-choice-'+label+'-check',command+['--check'],extra=tool_env)

    registry = work/'cargo-registry'; registry.mkdir()
    run('vendor-ordinary-dependencies', ['cargo', 'vendor', '--locked', '--versioned-dirs', registry], ROOT)
    # Development bootstrap source feed, separate from all production SDK packages.
    with tarfile.open(work/'cargo-dependencies.tar.gz','w:gz') as archive:
        for dependency in sorted(registry.iterdir()):archive.add(dependency,arcname=dependency.name)
    cargo_home = work/'cargo-home'; cargo_home.mkdir()
    (cargo_home/'config.toml').write_text('[source.crates-io]\nreplace-with="sdk"\n[source.sdk]\ndirectory='+json.dumps(str(registry))+'\n')
    cargo_env = dict(CARGO_HOME=str(cargo_home), CARGO_TARGET_DIR=str(work/'cargo-target'))
    workspace = tomllib.loads((ROOT/'Cargo.toml').read_text())['workspace']['package']; inventory = [dict(package='cargo-dependencies', archive=str(work/'cargo-dependencies.tar.gz'), sha256=hashlib.sha256((work/'cargo-dependencies.tar.gz').read_bytes()).hexdigest())]
    crate_sources = [ROOT/'crates'/n for n in ['instrument-api', 'instrument-runtime', 'instrument-scpi', 'instrument-testkit', 'instrument']]+[ROOT/'validation/driver-sdk/rust-provider']
    for source in crate_sources:
        manifest = (source/'Cargo.toml').read_text()
        for key, value in workspace.items():
            manifest = re.sub(rf'(?m)^{re.escape(key)}\.workspace\s*=\s*true\s*$', f'{key} = {json.dumps(value)}', manifest)
        # Preserve the real requirement verbatim; do not strengthen proof metadata.
        manifest = re.sub(r'(?m)^(instrument-[\w-]+)\s*=\s*\{\s*path\s*=\s*"[^"]+",\s*version\s*=\s*"([^"]+)"\s*\}', r'\1 = "\2"', manifest)
        manifest=re.sub(r'(?m)^(instrument-[\w-]+)\s*=\s*\{\s*path\s*=\s*"[^"]+",\s*version\s*=\s*"([^"]+)"\s*\}',r'\1 = "\2"',manifest)
        if source.name=='instrument':manifest=manifest.replace('readme = "../../README.md"','readme = "README.md"')
        if '[workspace]' not in manifest: manifest += '\n[workspace]\n'
        meta = tomllib.loads(manifest); name = meta['package']['name']; version = meta['package']['version']
        check_rust_pins(meta)
        stage = work/'rust-packages'/name; shutil.copytree(source/'src', stage/'src', ignore=IGNORE); (stage/'Cargo.toml').write_text(manifest)
        if source.name=='instrument':shutil.copy2(ROOT/'README.md',stage/'README.md')
        if 'path =' in manifest: raise RuntimeError('Source path dependency in staged crate')
        run('pack-'+name, ['cargo', 'package', '--offline', '--allow-dirty', '--no-verify'], stage, cargo_env)
        archive = work/'cargo-target/package'/f'{name}-{version}.crate'
        with tarfile.open(archive) as packed:
            check_rust_pins(tomllib.loads(packed.extractfile(f'{name}-{version}/Cargo.toml').read().decode('utf-8')))
            for entry in packed.getmembers():
                if registry not in (registry/entry.name).resolve().parents or entry.issym() or entry.islnk(): raise RuntimeError('Invalid archive path')
            packed.extractall(registry, filter='data')
        directory = registry/f'{name}-{version}'
        files = {str(p.relative_to(directory)).replace('\\','/'):hashlib.sha256(p.read_bytes()).hexdigest() for p in directory.rglob('*') if p.is_file()}
        digest = hashlib.sha256(archive.read_bytes()).hexdigest(); (directory/'.cargo-checksum.json').write_text(json.dumps(dict(files=files, package=digest)))
        inventory.append(dict(package=name, version=version, archive=str(archive), sha256=digest, files=sorted(files)))
    for label, source in [('binary', ROOT/'validation/driver-sdk/rust-consumer'), ('scpi', ROOT/'validation/scpi/rust-consumer')]:
        consumer = work/('rust-'+label+'-consumer'); shutil.copytree(source, consumer, ignore=IGNORE)
        if label == 'scpi':
            shutil.copy2(ROOT/'validation/scpi/endpoint-proof.rs',consumer/'src/endpoint_proof.rs')
            for f in ['profiles.rs', 'cases.rs']: shutil.copy2(generated/f, consumer/'src'/f)
            for original,target in [('profiles.rs','choice_profiles.rs'),('cases.rs','choice_cases.rs')]:shutil.copy2(choice_generated/original,consumer/'src'/target)
        for feature in ['', 'async']:
            command = ['cargo', 'run', '--offline', '--release']+(['--features', feature] if feature else [])
            run('rust-'+label+'-'+(feature or 'default'), command, consumer, cargo_env)
            run('rust-'+label+'-'+(feature or 'default')+'-clippy', ['cargo','clippy','--offline','--release']+(['--features',feature] if feature else [])+['--','-D','warnings'], consumer, cargo_env)
    for name in ['instrument-synthetic-driver-'+SDK_VERSION,'instrument-testkit-'+SDK_VERSION]: shutil.rmtree(registry/name)
    core = work/'rust-core-only'; (core/'src').mkdir(parents=True)
    (core/'Cargo.toml').write_text(core_only_manifests(SDK_VERSION,args.framework)[0])
    (core/'src/main.rs').write_text('fn main(){instrument_runtime::DriverRegistry::new([]).unwrap(); println!("PASS standards core only");}\n')
    for feature in ['', 'async']:
        flags=['--features',feature] if feature else []
        run('rust-core-'+(feature or 'default'), ['cargo','run','--offline','--release']+flags, core, cargo_env)
        closure=run('rust-core-'+(feature or 'default')+'-dependencies',['cargo','tree','--offline']+flags,core,cargo_env)
        if any(n in closure.lower() for n in ['synthetic','testkit','instrument-core ','visa']): raise RuntimeError('Forbidden core dependency')
        if not feature and 'tokio' in closure:raise RuntimeError('Default core unexpectedly requires Tokio')

    feed=work/'nuget-feed';feed.mkdir()
    (work/'dotnet-pack-sources/dotnet').mkdir(parents=True)
    shutil.copy2(ROOT/'dotnet/DriverSdk.Package.targets',work/'dotnet-pack-sources/dotnet/DriverSdk.Package.targets')
    shutil.copy2(ROOT/'dotnet/README.md',work/'dotnet-pack-sources/dotnet/README.md')
    expected={'InstrumentComponents.Abstractions':set(),'InstrumentComponents.Runtime':{'InstrumentComponents.Abstractions'},'InstrumentComponents.Scpi':{'InstrumentComponents.Abstractions'},'InstrumentComponents.TestKit':{'InstrumentComponents.Scpi'},'InstrumentComponents.SyntheticDriver':{'InstrumentComponents.Abstractions'},'InstrumentComponents':{'InstrumentComponents.Runtime'}}
    for name in expected:
        source=ROOT/'validation/driver-sdk/dotnet-provider' if name.endswith('SyntheticDriver') else ROOT/'dotnet/src'/name
        stage=work/'dotnet-pack-sources'/source.relative_to(ROOT)
        shutil.copytree(source,stage,ignore=IGNORE)
    # Preserve SDK-internal relative references while packing outside the checkout.
    for name in expected:
        source=ROOT/'validation/driver-sdk/dotnet-provider' if name.endswith('SyntheticDriver') else ROOT/'dotnet/src'/name
        stage=work/'dotnet-pack-sources'/source.relative_to(ROOT)
        run('nuget-pack-'+name,['dotnet','pack',stage,'-c','Release','-o',feed],work)
    for archive in sorted(feed.glob('*.nupkg')):
        with zipfile.ZipFile(archive) as packed:
            metadata=ET.fromstring(packed.read(next(n for n in packed.namelist() if n.endswith('.nuspec')))); name=metadata.find('.//{*}id').text
            dependencies={d.attrib['id'] for d in metadata.findall('.//{*}dependency')}
            if dependencies != expected[name]:raise RuntimeError(f'Unexpected {name} dependencies: {dependencies}')
            ranges={d.attrib['id']:d.attrib['version'] for d in metadata.findall('.//{*}dependency')}
            check_nuget_pins(ranges)
            if any(n.endswith(('.json','.toml')) or 'vendors/' in n for n in packed.namelist()):raise RuntimeError('Driver data in SDK package')
            inventory.append(dict(package=name,archive=str(archive),sha256=hashlib.sha256(archive.read_bytes()).hexdigest(),dependencies=ranges,files=packed.namelist()))
    aot=work/'dotnet-consumer';shutil.copytree(ROOT/'validation/driver-sdk/dotnet-consumer',aot,ignore=IGNORE)
    shutil.copy2(ROOT/'validation/scpi/EndpointProof.cs',aot/'EndpointProof.cs')
    # Restore evaluates the project's declared framework independently of publish's
    # TargetFramework override. Keep the copied project and selected target equal.
    project=aot/'AotConsumer.csproj'
    project.write_text(project.read_text().replace('<TargetFramework>net8.0</TargetFramework>', '<TargetFramework>'+args.framework+'</TargetFramework>'))
    for f in ['GeneratedProfiles.cs','GeneratedCases.cs']:shutil.copy2(generated/f,aot/f)
    for f in ['GeneratedProfiles.cs','GeneratedCases.cs']:shutil.copy2(choice_generated/f,aot/('Choice'+f))
    config=aot/'NuGet.Config'
    config.write_text(f'<configuration><packageSources><clear/><add key="local" value="{feed}"/><add key="nuget" value="https://api.nuget.org/v3/index.json"/></packageSources><packageSourceMapping><packageSource key="local"><package pattern="InstrumentComponents*"/></packageSource><packageSource key="nuget"><package pattern="Microsoft.*"/><package pattern="runtime.*"/><package pattern="System.*"/></packageSource></packageSourceMapping></configuration>\n')
    nuget_env=dict(NUGET_PACKAGES=str(work/'fresh-nuget-cache'))
    publish=['dotnet','publish','AotConsumer.csproj','-c','Release','-r',rid,'--framework',args.framework,'--self-contained','true','-o',work/'native','--configfile',config]
    if args.cpp_compiler:publish.append('-p:CppCompilerAndLinker='+args.cpp_compiler)
    run('native-aot-publish',publish,aot,nuget_env,diagnostics=True)
    assets=json.loads((aot/'obj/project.assets.json').read_text())
    if not all(v['type']=='package' for v in assets['libraries'].values()):raise RuntimeError('ProjectReference leaked into consumer')
    binary=work/'native'/('AotConsumer.exe' if windows else 'AotConsumer')
    run('native-aot-behavior',[binary])
    # Each run creates hundreds of dedicated workers. Repetition exercises native
    # thread-ID reuse and late async disposal; every invocation must exit zero.
    run('native-aot-lifecycle-repeat-1',[binary])
    run('native-aot-lifecycle-repeat-2',[binary])
    if windows:
        data=binary.read_bytes();offset=struct.unpack_from('<I',data,0x3c)[0];machine=struct.unpack_from('<H',data,offset+4)[0];optional=offset+24
        if data[offset:offset+4]!=b'PE\0\0' or machine!=0x8664 or struct.unpack_from('<H',data,optional)[0]!=0x20b or struct.unpack_from('<II',data,optional+112+14*8)!=(0,0):raise RuntimeError('Expected native x64 PE without CLR header')
        (work/'native-dependencies.json').write_text(json.dumps(dict(format='PE32+',machine='x64',clrHeader=False,executed=True)))
    else:run('native-dependencies',['ldd',binary])
    if args.isolate_runtime:
        sandbox=['bwrap','--die-with-parent','--unshare-net','--ro-bind','/usr','/usr','--ro-bind','/lib','/lib','--ro-bind','/lib64','/lib64','--proc','/proc','--dev','/dev']
        for path in ['/usr/share/dotnet','/usr/lib/dotnet','/usr/local/share/dotnet','/usr/local/dotnet']:
            if Path(path).is_dir():sandbox+=['--tmpfs',path]
        sandbox+=['--ro-bind',str(work/'native'),'/app','--clearenv','--setenv','PATH','/usr/bin:/bin','--','/app/AotConsumer']
        if args.isolation_sudo:sandbox=['sudo','--']+sandbox
        run('native-runtime-isolation',sandbox)
    probe=work/'warning-probe';shutil.copytree(aot,probe,ignore=IGNORE)
    with (probe/'Program.cs').open('a') as code:code.write('\nWarningProbe.Trigger();\n')
    (probe/'WarningProbe.cs').write_text('using System.Diagnostics.CodeAnalysis; internal static class WarningProbe { [RequiresUnreferencedCode("Intentional diagnostic gate test")][RequiresDynamicCode("Intentional AOT diagnostic gate test")] internal static void Trigger() {} }\n')
    warning=[str(x) for x in publish];warning[warning.index(str(work/'native'))]=str(work/'warning-native');warning[warning.index(str(config))]=str(probe/'NuGet.Config')
    run('negative-trimming-gate',warning,probe,nuget_env,negative=True)
    core_dotnet=work/'dotnet-core-only';core_dotnet.mkdir();core_feed=work/'core-only-feed';core_feed.mkdir()
    core_names={'InstrumentComponents.Abstractions','InstrumentComponents.Runtime','InstrumentComponents.Scpi'}
    for name in core_names:shutil.copy2(feed/(name+'.'+SDK_VERSION+'.nupkg'),core_feed)
    (core_dotnet/'CoreOnly.csproj').write_text(core_only_manifests(SDK_VERSION,args.framework)[1])
    (core_dotnet/'Program.cs').write_text('new InstrumentComponents.Runtime.DriverRegistry(System.Array.Empty<InstrumentComponents.Drivers.IDriverFactory>());System.Console.WriteLine("PASS standards core only");')
    (core_dotnet/'NuGet.Config').write_text(config.read_text().replace(str(feed),str(core_feed)))
    run('dotnet-core-only',['dotnet','run','--project','CoreOnly.csproj','-c','Release'],core_dotnet,dict(NUGET_PACKAGES=str(work/'fresh-core-only-cache')))
    names={n.split('/')[0] for n in json.loads((core_dotnet/'obj/project.assets.json').read_text())['libraries']}
    if not core_names.issubset(names) or any(n not in core_names and not n.startswith('Microsoft.') for n in names):raise RuntimeError('Unexpected core closure: '+str(names))
    for archive in python_feed.glob('instrument_driver_tools-*.whl'):inventory.append(dict(package='instrument-driver-tools',archive=str(archive),sha256=hashlib.sha256(archive.read_bytes()).hexdigest()))
    (work/'packages.json').write_text(json.dumps(inventory,indent=2)+'\n')
    (work/'platform.json').write_text(json.dumps(dict(rid=rid,framework=args.framework,nativeExecuted=True,isolationExecuted=args.isolate_runtime,binary=str(binary),sha256=hashlib.sha256(binary.read_bytes()).hexdigest()),indent=2)+'\n')
    print(f'PASS {rid}/{args.framework}: installed tool, packed default/async SDK/TestKit, generated consumers, core closure, native AOT and warning gate. Isolation={args.isolate_runtime}',flush=True)


if __name__=='__main__':main()
