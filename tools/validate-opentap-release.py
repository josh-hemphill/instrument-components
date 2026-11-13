#!/usr/bin/env python3
"""Install an exact TapPackage into a copied CLI runtime and execute a managed mock plan."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import xml.etree.ElementTree as ET
from xml.sax.saxutils import quoteattr
import zipfile
ROOT=Path(__file__).resolve().parents[1]
def sha(file):return hashlib.sha256(file.read_bytes()).hexdigest()
def main():
    p=argparse.ArgumentParser(description=__doc__)
    p.add_argument('--package',required=True,type=Path)
    p.add_argument('--runtime',required=True,type=Path,help='Built OpenTAP CLI runtime directory; copied before use')
    p.add_argument('--output',required=True,type=Path)
    a=p.parse_args();out=a.output.resolve();package=a.package.resolve()
    if out==ROOT or ROOT in out.parents or out in ROOT.parents or out.exists() and any(out.iterdir()):raise ValueError('Separate empty output directory required')
    version=json.loads((ROOT/'spec/driver-sdk-release.json').read_text())['version']
    with zipfile.ZipFile(package) as archive:
        names=archive.namelist();required=['InstrumentComponents.OpenTap.dll','InstrumentComponents.dll','InstrumentComponents.Abstractions.dll','InstrumentComponents.Runtime.dll']
        if not set(required)<=set(names) or any('visa' in name.lower() or 'drivers.' in name.lower() for name in names):raise ValueError('Unexpected TapPackage closure')
        meta=ET.fromstring(archive.read('Packages/InstrumentComponents.OpenTap/package.xml'))
        if meta.attrib['Version']!=version:raise ValueError('TapPackage version mismatch')
        expected={name:hashlib.sha256(archive.read(name)).hexdigest() for name in required}
    out.mkdir(parents=True);runtime=out/'runtime';shutil.copytree(a.runtime,runtime)
    env=dict(os.environ,DOTNET_ROLL_FORWARD='LatestMajor',DOTNET_CLI_HOME=str(out/'dotnet-home'),NUGET_PACKAGES=str(out/'nuget-cache'),NUGET_HTTP_CACHE_PATH=str(out/'nuget-http-cache'))
    private_runtime=out/'runtime-private';private_runtime.mkdir(mode=0o700);env['XDG_RUNTIME_DIR']=str(private_runtime);env['XDG_DATA_HOME']=str(out/'data')
    records=[]
    def run(name,command,cwd=runtime):
        log=out/(name+'.log')
        with log.open('w') as stream:r=subprocess.run(list(map(str,command)),cwd=cwd,env=env,stdout=stream,stderr=subprocess.STDOUT)
        records.append(dict(name=name,command=list(map(str,command)),exitCode=r.returncode,log=log.name,sha256=sha(log)))
        (out/'execution.json').write_text(json.dumps(records,indent=2)+'\n')
        if r.returncode:raise RuntimeError(name+': '+log.read_text(errors='replace')[-6000:])
        return log.read_text(errors='replace')
    tap=runtime/('tap.exe' if os.name=='nt' else 'tap')
    if 'InstrumentComponents.OpenTap' not in run('initial-installed-list',[tap,'package','list','-i']):raise ValueError('Runtime must initially contain built plugin for uninstall check')
    run('uninstall',[tap,'package','uninstall','InstrumentComponents.OpenTap','-f'])
    if (runtime/'InstrumentComponents.OpenTap.dll').exists():raise ValueError('Uninstall left plugin DLL')
    run('install',[tap,'package','install',package])
    run('verify',[tap,'package','verify','InstrumentComponents.OpenTap'])
    run('show',[tap,'package','show','InstrumentComponents.OpenTap'])
    if 'InstrumentComponents.OpenTap' not in run('installed-list',[tap,'package','list','-i']):raise ValueError('Installed plugin missing')
    if any(sha(runtime/name)!=expected[name] for name in required):raise ValueError('Installed DLL bytes differ from exact TapPackage')
    app=out/'mock';app.mkdir();shutil.copy2(ROOT/'validation/opentap-release/Program.cs',app/'Program.cs');shutil.copy2(ROOT/'validation/neutral/OpaqueDevice.cs',app/'OpaqueDevice.cs')
    references=''.join('<Reference Include='+quoteattr(Path(name).stem)+'><HintPath>'+str(runtime/name).replace('&','&amp;').replace('<','&lt;')+'</HintPath></Reference>' for name in required)
    (app/'Installed.csproj').write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net8.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable><TreatWarningsAsErrors>true</TreatWarningsAsErrors></PropertyGroup><ItemGroup><PackageReference Include="OpenTAP" Version="9.32.2"/>'+references+'</ItemGroup></Project>')
    output=run('managed-installed-mock-plan',['dotnet','run','--project',app/'Installed.csproj','-c','Release'],app)
    if 'PASS installed managed OpenTAP plugin discovery' not in output:raise ValueError('No managed execution marker')
    (out/'proof.json').write_text(json.dumps(dict(mode='installed-TapPackage-managed',version=version,packageSha256=sha(package),installedDllSha256=expected,mockSourceSha256=sha(ROOT/'validation/opentap-release/Program.cs'),providerSourceSha256=sha(ROOT/'validation/neutral/OpaqueDevice.cs'),hardwareValidated=False,nativeAotClaim=False),indent=2)+'\n')
    print('PASS managed installed TapPackage, uninstall/reinstall and saved/loaded mock lifecycle')
if __name__=='__main__':main()
