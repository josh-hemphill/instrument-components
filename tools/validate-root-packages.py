#!/usr/bin/env python3
"""Qualify standard core/VISA/OpenTAP package archives from explicit SDK feeds.

Rust VISA is compile checked without loading a VISA provider. OpenTAP and its
VISA host are managed software; their package-only mock plan has no AOT claim.
No external device repository or physical instrument is used by this gate.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import platform
import re
import shutil
import subprocess
import tomllib
import xml.etree.ElementTree as ET
import zipfile
from instrument_driver_tools.feeds import prepare, extract, VERSION

ROOT = Path(__file__).resolve().parents[1]
IGNORE = shutil.ignore_patterns('.git', 'target', 'bin', 'obj', '__pycache__')
RUST = ['instrument-core', 'instrument-visa']
DOTNET = ['InstrumentComponents.Visa', 'InstrumentComponents.OpenTap', 'InstrumentComponents.OpenTap.Visa']


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--sdk-proof', required=True, type=Path)
    parser.add_argument('--output', required=True, type=Path)
    parser.add_argument('--framework', required=True, choices=['net8.0', 'net10.0'])
    parser.add_argument('--development-inputs', action='store_true')
    args = parser.parse_args()
    dirty = bool(subprocess.check_output(['git', 'status', '--porcelain'], cwd=ROOT, text=True).strip())
    if dirty and not args.development_inputs:
        raise ValueError('Committed source required; use explicit development mode for local iteration')
    mode = 'local-development-sdk-artifact-bootstrap' if args.development_inputs else 'pinned-sdk-artifact-bootstrap'
    out, sdk = args.output.resolve(), args.sdk_proof.resolve()
    if out == ROOT or ROOT in out.parents or out in ROOT.parents or out == sdk:
        raise ValueError('Separate output outside the source checkout required')
    if out.exists() and any(out.iterdir()):
        raise ValueError('Empty output required')
    if platform.system() not in ['Windows', 'Linux'] or platform.machine().lower() not in ['amd64', 'x86_64']:
        raise ValueError('Native Windows/Linux x64 host required')
    out.mkdir(parents=True, exist_ok=True)
    transfer = out / 'sdk-transfer'
    transfer.mkdir()
    rows = json.loads((sdk / 'packages.json').read_text(encoding='utf-8'))
    for row in rows:
        basename = PurePosixPath(row['archive'].replace('\\', '/')).name
        declared = Path(row['archive']).resolve()
        candidates = [declared] if declared.is_relative_to(sdk) and declared.is_file() else [p for p in sdk.rglob(basename) if p.is_file()]
        if len(candidates) != 1 or sha(candidates[0]) != row['sha256']:
            raise ValueError('Missing, ambiguous or changed SDK input: ' + basename)
        shutil.copy2(candidates[0], transfer / basename)
    shutil.copy2(sdk / 'packages.json', transfer / 'packages.json')
    feed = prepare(transfer, out / 'feeds')
    registry, nuget = Path(feed['cargoRegistry']), Path(feed['nugetFeed'])
    env = dict(os.environ, CARGO_HOME=feed['CARGO_HOME'], CARGO_TARGET_DIR=str(out / 'cargo-target'),
               NUGET_PACKAGES=str(out / 'nuget-cache'), NUGET_HTTP_CACHE_PATH=str(out / 'nuget-http'),
               DOTNET_CLI_HOME=str(out / 'dotnet-home'), DOTNET_NOLOGO='1', DOTNET_CLI_TELEMETRY_OPTOUT='1')
    execution = []

    def run(name, command, cwd=out):
        log = out / (name + '.log')
        print(name, flush=True)
        with log.open('w', encoding='utf-8') as stream:
            result = subprocess.run(list(map(str, command)), cwd=cwd, env=env, stdout=stream, stderr=subprocess.STDOUT)
        execution.append(dict(name=name, command=list(map(str, command)), exitCode=result.returncode, log=log.name, sha256=sha(log)))
        (out / 'execution.json').write_text(json.dumps(execution, indent=2) + '\n', encoding='utf-8')
        if result.returncode:
            raise RuntimeError(name + ': ' + log.read_text(encoding='utf-8', errors='replace')[-10000:])

    workspace = out / 'source-workspace'
    workspace.mkdir()
    for name in ['Cargo.toml', 'Cargo.lock', 'README.md', 'LICENSE-MIT', 'LICENSE-APACHE']:
        shutil.copy2(ROOT / name, workspace / name)
    shutil.copytree(ROOT / 'crates', workspace / 'crates', ignore=IGNORE)
    packages = out / 'packages'
    packages.mkdir()
    records = []
    for name in RUST:
        run('pack-' + name, ['cargo', 'package', '--offline', '--allow-dirty', '--no-verify', '-p', name], workspace)
        archive = out / 'cargo-target/package' / (name + '-' + VERSION + '.crate')
        extract(archive, registry)
        directory = registry / (name + '-' + VERSION)
        manifest = tomllib.loads((directory / 'Cargo.toml').read_text(encoding='utf-8'))
        if manifest['package']['name'] != name or manifest['package']['version'] != VERSION:
            raise ValueError('Root Rust identity mismatch')
        for dependency, value in manifest.get('dependencies', {}).items():
            if 'path' in value or 'git' in value or dependency.startswith('instrument-') and value['version'] != '=' + VERSION:
                raise ValueError('Root Rust dependency boundary')
        members = {str(p.relative_to(directory)).replace('\\', '/'): sha(p) for p in directory.rglob('*') if p.is_file()}
        for relative in members:
            if relative.startswith('src/') and (directory / relative).read_bytes().replace(b'\r\n', b'\n') != (ROOT / 'crates' / name / relative).read_bytes().replace(b'\r\n', b'\n'):
                raise ValueError('Packaged root Rust source differs')
        (directory / '.cargo-checksum.json').write_text(json.dumps(dict(files=members, package=sha(archive))), encoding='utf-8')
        shutil.copy2(archive, packages / archive.name)
        records.append(dict(package=name, ecosystem='Cargo', file=archive.name, sha256=sha(archive), dependencies=manifest.get('dependencies', {}), members=members))
    # Executable byte-level mock check of the actual instrument-core archive.
    core = out / 'core-consumer'
    (core / 'src').mkdir(parents=True)
    (core / 'Cargo.toml').write_text('[package]\nname="root-core-package-proof"\nversion="0.0.0"\nedition="2021"\npublish=false\n[workspace]\n[dependencies]\ninstrument-core="=' + VERSION + '"\ntokio={version="1",features=["rt"],optional=true}\n[features]\ndefault=[]\nasync=["instrument-core/async","dep:tokio"]\n', encoding='utf-8')
    shutil.copy2(ROOT / 'validation/root-packages/core.rs', core / 'src/main.rs')
    for flags in [[], ['--features', 'async']]:
        run('core-async' if flags else 'core-default', ['cargo', 'run', '--offline', '--release'] + flags, core)
    visa = out / 'visa-consumer'
    (visa / 'src').mkdir(parents=True)
    (visa / 'src/main.rs').write_text('use instrument_visa as _;fn main(){}\n', encoding='utf-8')
    (visa / 'Cargo.toml').write_text('[package]\nname="root-visa-package-proof"\nversion="0.0.0"\nedition="2021"\npublish=false\n[workspace]\n[dependencies]\ninstrument-visa="=' + VERSION + '"\n[features]\ndefault=[]\ntokio=["instrument-visa/tokio"]\n', encoding='utf-8')
    for flags in [[], ['--features', 'tokio']]:
        run('visa-tokio-check' if flags else 'visa-default-check', ['cargo', 'check', '--offline'] + flags, visa)
    for label, consumer, flags in [('core', core, ['--features', 'async']), ('visa', visa, ['--features', 'tokio'])]:
        run(label + '-package-metadata', ['cargo', 'metadata', '--offline', '--format-version', '1'] + flags, consumer)
        metadata = json.loads((out / (label + '-package-metadata.log')).read_text(encoding='utf-8'))
        for package in metadata['packages']:
            if package['name'].startswith('instrument-'):
                if package['name'] not in RUST or package['version'] != VERSION or not (package.get('source') or '').startswith('registry+'):
                    raise ValueError('Root Rust consumer contains a source/unexpected implementation dependency')
    staged_dotnet = out / 'source-dotnet'
    shutil.copytree(ROOT / 'dotnet/src', staged_dotnet / 'src', ignore=IGNORE)
    shutil.copy2(ROOT / 'dotnet/README.md', staged_dotnet / 'README.md')
    shutil.copy2(ROOT / 'dotnet/DriverSdk.Package.targets', staged_dotnet / 'DriverSdk.Package.targets')
    expected_dependencies = {
        'InstrumentComponents.Visa': {'InstrumentComponents': '[' + VERSION + ']', 'IviFoundation.Visa': '8.0.2'},
        'InstrumentComponents.OpenTap': {'InstrumentComponents': '[' + VERSION + ']', 'OpenTAP': '9.32.2'},
        'InstrumentComponents.OpenTap.Visa': {'InstrumentComponents.Scpi': '[' + VERSION + ']', 'InstrumentComponents.OpenTap': '[' + VERSION + ']', 'InstrumentComponents.Visa': '[' + VERSION + ']'},
    }
    for name in DOTNET:
        project = staged_dotnet / 'src' / name / (name + '.csproj')
        run('pack-' + name, ['dotnet', 'pack', project, '-c', 'Release', '-o', nuget])
        archive = nuget / (name + '.' + VERSION + '.nupkg')
        with zipfile.ZipFile(archive) as z:
            manifest = ET.fromstring(z.read(next(n for n in z.namelist() if n.endswith('.nuspec'))))
            deps = {d.attrib['id']: d.attrib['version'] for d in manifest.findall('.//{*}dependency')}
            if manifest.findtext('.//{*}id') != name or manifest.findtext('.//{*}version') != VERSION or deps != expected_dependencies[name]:
                raise ValueError('Root NuGet metadata/dependency boundary: ' + name + ' ' + str(deps))
            if {Path(n).name for n in z.namelist() if n.endswith('.dll')} != {name + '.dll'}:
                raise ValueError('Unexpected root NuGet DLL closure')
            if z.read('README.md').replace(b'\r\n', b'\n') != (ROOT / 'dotnet/README.md').read_bytes().replace(b'\r\n', b'\n'):
                raise ValueError('Packaged README differs from source')
        shutil.copy2(archive, packages / archive.name)
        records.append(dict(package=name, ecosystem='NuGet', file=archive.name, sha256=sha(archive), dependencies=deps, members=z.namelist()))
    managed = out / 'managed-consumer'
    managed.mkdir()
    program = (ROOT / 'validation/opentap-release/Program.cs').read_text(encoding='utf-8')
    marker = '// This managed fixture'
    assertions = 'if(typeof(InstrumentComponents.Visa.VisaSessionOpener).Assembly.GetName().Name!="InstrumentComponents.Visa"||typeof(InstrumentComponents.OpenTap.Visa.OpenTapVisaDeviceProvider).Assembly.GetName().Name!="InstrumentComponents.OpenTap.Visa")throw new InvalidOperationException("Root package assemblies missing");\n'
    program = program.replace(marker, assertions + marker).replace('references DLLs installed from the TapPackage, never projects.', 'restores released-form package DLLs, never source projects.').replace('PASS installed managed OpenTAP plugin discovery', 'PASS package-only managed OpenTAP plugin discovery')
    (managed / 'Program.cs').write_text(program, encoding='utf-8')
    shutil.copy2(ROOT / 'validation/neutral/OpaqueDevice.cs', managed / 'OpaqueDevice.cs')
    references = ''.join('<PackageReference Include="' + n + '" Version="[' + VERSION + ']"/>' for n in ['InstrumentComponents'] + DOTNET)
    (managed / 'Root.csproj').write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>' + args.framework + '</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable><TreatWarningsAsErrors>true</TreatWarningsAsErrors></PropertyGroup><ItemGroup>' + references + '</ItemGroup></Project>', encoding='utf-8')
    shutil.copy2(feed['nugetConfig'], managed / 'NuGet.Config')
    run('managed-package-only-plan', ['dotnet', 'run', '--project', managed / 'Root.csproj', '-c', 'Release'])
    assets = json.loads((managed / 'obj/project.assets.json').read_text(encoding='utf-8'))
    if any(row['type'] != 'package' for row in assets['libraries'].values()):
        raise ValueError('Root consumer contains a source project reference')
    consumed = []
    for key in assets['libraries']:
        name, version = key.split('/')
        if not name.startswith('InstrumentComponents'):
            continue
        archive = Path(env['NUGET_PACKAGES']) / name.lower() / version / (name.lower() + '.' + version + '.nupkg')
        expected = next((r['sha256'] for r in records if r['package'] == name), feed['packages'].get(name))
        if version != VERSION or sha(archive) != expected:
            raise ValueError('Consumed root NuGet archive identity mismatch')
        consumed.append(dict(package=name, version=version, sha256=sha(archive)))
    (out / 'packages.json').write_text(json.dumps(dict(mode=mode, version=VERSION, sourceCommit=subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=ROOT, text=True).strip(), packages=records, sdkPackages=feed['packages']), indent=2) + '\n', encoding='utf-8')
    source_files = [ROOT / 'dotnet/README.md', ROOT / 'validation/root-packages/core.rs', ROOT / 'validation/opentap-release/Program.cs', ROOT / 'validation/neutral/OpaqueDevice.cs']
    source_files += [p for p in (ROOT / 'dotnet/src').rglob('*') if p.is_file() and p.suffix in ['.cs', '.csproj'] and not {'bin', 'obj'} & set(p.parts)]
    (out / 'proof.json').write_text(json.dumps(dict(mode=mode, framework=args.framework, sourceDirty=dirty, platform=platform.system(), packages=records, consumedNuGet=consumed, sourceFiles={str(p.relative_to(ROOT)): sha(p) for p in source_files}, managedPlanExecuted=True, rustCoreDefaultAsyncExecuted=True, visaDefaultTokioCompileChecked=True, actualVisaProviderActivated=False, openTapNativeAotClaim=False, hardwareValidated=False), indent=2) + '\n', encoding='utf-8')
    print('PASS five standalone root payloads, Rust core execution/VISA compilation and managed package-only plan; no hardware')


if __name__ == '__main__':
    main()
