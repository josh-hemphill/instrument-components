#!/usr/bin/env python3
"""Check exact public SDK versions before dependent release jobs, never publish."""
import json
from pathlib import Path
import urllib.error
import urllib.request
ROOT=Path(__file__).resolve().parents[1]
manifest=json.loads((ROOT/'spec/driver-sdk-release.json').read_text());version=manifest['version']
missing=[]
for name in manifest['rustPackages']:
    request=urllib.request.Request('https://crates.io/api/v1/crates/'+name+'/'+version,headers={'User-Agent':'instrument-components release prerequisites (github.com/josh-hemphill/instrument-components)'})
    try:
        with urllib.request.urlopen(request,timeout=30) as response:record=json.load(response)
        if record['version']['num']!=version or record['version']['yanked']:raise ValueError('Wrong/yanked SDK version: '+name)
    except urllib.error.HTTPError as error:
        if error.code!=404:raise
        missing.append(name)
for name in manifest['nugetPackages']:
    try:
        with urllib.request.urlopen('https://api.nuget.org/v3-flatcontainer/'+name.lower()+'/index.json',timeout=30) as response:record=json.load(response)
        if version not in record['versions']:missing.append(name)
    except urllib.error.HTTPError as error:
        if error.code!=404:raise
        missing.append(name)
if missing:raise SystemExit('Publish and validate the SDK preview first. Missing public '+version+': '+', '.join(missing))
print('PASS exact public SDK prerequisite versions; this is availability, not consumer execution evidence')
