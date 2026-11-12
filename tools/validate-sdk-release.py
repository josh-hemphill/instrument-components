#!/usr/bin/env python3
"""Validate independently versioned SDK previews. No publication or hardware I/O."""
import argparse
import json
from pathlib import Path
import re
import runpy
import tomllib
import xml.etree.ElementTree as ET

ROOT=Path(__file__).resolve().parent.parent


def validate(root, tag=None):
    manifest=json.loads((root/'spec/driver-sdk-release.json').read_text()); version=manifest['version']
    match=re.fullmatch(r'(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)-(alpha|beta|rc)\.(0|[1-9][0-9]*)',version)
    if not match:raise ValueError('SDK delivery is prerelease-only; use alpha/beta/rc.N')
    if tag is not None and tag!=manifest['tagPrefix']+version:raise ValueError('Tag does not match SDK preview version')
    pep='.'.join(match.groups()[:3])+{'alpha':'a','beta':'b','rc':'rc'}[match[4]]+match[5]
    if manifest['pythonVersion']!=pep:raise ValueError('Python and SDK preview versions disagree')
    if set(manifest['frameworks'])!={'net8.0','net10.0'} or set(manifest['runtimeIdentifiers'])!={'linux-x64','win-x64'}:raise ValueError('Both native platforms/frameworks are required')
    major=manifest['contractMajor']
    for file,pattern in [('crates/instrument-api/src/lib.rs',r'CONTRACT_MAJOR: u32 = (\d+)'),('dotnet/src/InstrumentComponents.Abstractions/Drivers.cs',r'const uint Major = (\d+)'),('tools/driver-tools/src/instrument_driver_tools/__init__.py',r'CONTRACT_MAJOR = (\d+)')]:
        actual=re.search(pattern,(root/file).read_text(encoding='utf-8'))
        if actual is None or int(actual[1])!=major:raise ValueError('SDK contract major drift: '+file)
    allowed=set(manifest['rustPackages'])
    for name in manifest['rustPackages']:
        cargo=tomllib.loads((root/'crates'/name/'Cargo.toml').read_text())
        if cargo['package'].get('version')!=version:raise ValueError('SDK package version drift: '+name)
        for dependency,settings in cargo.get('dependencies',{}).items():
            if dependency.startswith('instrument-'):
                if dependency not in allowed:raise ValueError('External/legacy implementation dependency: '+name)
                if not isinstance(settings,dict) or settings.get('version')!='='+version:raise ValueError('SDK dependency must pin exact preview: '+name)
        if name!='instrument-testkit' and 'instrument-testkit' in cargo.get('dependencies',{}):raise ValueError('Production package depends on TestKit')
    for name in manifest['nugetPackages']:
        project=ET.parse(root/'dotnet/src'/name/(name+'.csproj')).getroot()
        if project.findtext('.//Version')!=version:raise ValueError('NuGet SDK version drift: '+name)
        if project.findall('.//ProjectReference') and not any(i.get('Project')=='../../DriverSdk.Package.targets' for i in project.findall('Import')):raise ValueError('NuGet exact SDK dependency policy missing: '+name)
        for reference in project.findall('.//ProjectReference'):
            dep=Path(reference.attrib['Include']).stem
            if dep not in manifest['nugetPackages'] or dep.endswith('TestKit') and name!='InstrumentComponents.TestKit':raise ValueError('External/test implementation dependency: '+name)
    package=tomllib.loads((root/'tools/driver-tools/pyproject.toml').read_text())
    if package['project']['version']!=pep:raise ValueError('Tool wheel version drift')
    tool=runpy.run_path(str(root/'tools/driver-tools/src/instrument_driver_tools/__init__.py'))
    if tool['VERSION']!=version or tool['python_version'](version)!=pep:raise ValueError('Tool runtime/wheel identity drift')
    init=(root/'tools/driver-tools/src/instrument_driver_tools/__init__.py').read_text()
    if f'VERSION = "{version}"' not in init:raise ValueError('Tool CLI version drift')
    if not manifest['publicationRequiresNativePlatformJobs'] or manifest['publicationRequiresHardwareEvidence'] or manifest['hardwareValidated']:raise ValueError('Preview evidence/publication policy mismatch')
    return version


if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__);parser.add_argument('--tag');parser.add_argument('--repository',type=Path,default=ROOT);args=parser.parse_args()
    try:print(validate(args.repository,args.tag))
    except (ValueError,KeyError,OSError) as error:parser.exit(1,str(error)+'\n')
