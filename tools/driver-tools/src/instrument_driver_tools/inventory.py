"""Main-repository migration inventory. Never infers hardware support from hints."""
import hashlib
import json
from pathlib import Path
import re
import tomllib

CLASSES = {
    "Dmm": "dmm", "DcPowerSupply": "dc_psu", "FunctionGenerator": "fgen",
    "Oscilloscope": "scope", "Switch": "switch", "Counter": "counter",
    "PowerMeter": "power_meter", "SpectrumAnalyzer": "spectrum_analyzer",
}



def inventory(root: Path) -> dict:
    sources = {}

    def read(relative):
        data = (root / relative).read_bytes()
        sources[relative] = hashlib.sha256(data).hexdigest()
        return data.decode()

    repositories = json.loads(read('spec/device-repositories.json'))
    registry = tomllib.loads(read("crates/instrument-core/data/model_registry.toml"))
    models = []
    seen = set()
    for entry in registry["entry"]:
        key = (entry["manufacturer"], entry["model"])
        if key in seen:
            raise ValueError(f"Duplicate model hint: {key}")
        seen.add(key)
        repository = repositories['manufacturers'][entry['manufacturer']]
        models.append(dict(**entry, repository=repository,
                           evidence="identity-hint-only", hardwareValidated=False,
                           migration="required"))
    profiles = []
    for path in sorted((root / "spec/vendors").glob("*.json")):
        data = json.loads(read(path.relative_to(root).as_posix()))
        profiles.append(dict(profile=data, evidence="source-and-transcripts; no-hardware-evidence",
                             repository=repositories['profileOwners'][data['id']]))
    dialects = tomllib.loads(read("crates/instrument-core/data/dialects/profiles.toml"))["profile"]
    class_operations = {}
    for kind, rust_name in CLASSES.items():
        files = {}
        for asynchronous in (False, True):
            rust_path = f"crates/instrument/src/classes/{'async_' if asynchronous else ''}{rust_name}.rs"
            text = read(rust_path)
            methods = sorted(set(re.findall(r"\bpub\s+(?:async\s+)?fn\s+(\w+)\s*\(", text)))
            if not methods:
                raise ValueError(f"No public methods inventoried: {rust_path}")
            files[rust_path] = methods
            cs_path = f"dotnet/src/InstrumentComponents/Classes/{'Async' if asynchronous else ''}{kind}.cs"
            text = read(cs_path)
            methods = sorted(set(re.findall(r"\bpublic\s+(?:(?:static|async|virtual|override)\s+)*[\w<>,?\[\]]+\s+(\w+)\s*\(", text)))
            if not methods:
                raise ValueError(f"No public methods inventoried: {cs_path}")
            files[cs_path] = methods
        class_operations[kind] = dict(required=True, migration="pending-new-typed-capabilities-and-facade", sources=files)
    return dict(schemaVersion=1, requiredTargets=["linux-x64", "win-x64"],
                apiCompatibility="not-required", previewBeforeHardware=True,
                modelHints=sorted(models, key=lambda m: (m["manufacturer"], m["model"])),
                usbHints=registry.get("usb_entry", []), deviceProfiles=profiles,
                genericProfileIds=sorted(p["id"] for p in dialects if p["id"].startswith("generic_")),
                classOperations=class_operations,
                repositories=sorted(set(m["repository"] for m in models)),
                hardwareEvidence="none; neither old nor new API is hardware-validated",
                sourceSha256=dict(sorted(sources.items())))


def canonical(data: dict) -> str:
    return json.dumps(data, indent=2, ensure_ascii=False, sort_keys=True) + "\n"

# This wrapper augments the model inventory with a mechanically reconciled public
# operation ledger. Any newly added legacy method must receive an explicit mapping.
_model_inventory = inventory

def inventory(root: Path) -> dict:
    result = _model_inventory(root)
    def read(relative):
        data = (root / relative).read_bytes(); result['sourceSha256'][relative] = hashlib.sha256(data).hexdigest(); return data.decode()
    manifest = json.loads(read('spec/driver-operations.json'))
    mapped = {(op['kind'], re.sub('[^a-z0-9]','',op['method'].lower())):op for op in manifest['operations']}
    common = {re.sub('[^a-z0-9]','',op['method'].lower()):op for op in manifest['operations'] if op['kind']=='Common'}
    removed = {re.sub('[^a-z0-9]','',name.lower()) for name in manifest['removedStructuralSymbols']}
    aliases = {'queryidn':'queryidentity','outputoff':'shutdown','setwaveform':'setwaveform','configureresistance2wire':'configureresistance','measureresistance2w':'measureresistance2wire','measureresistance4w':'measureresistance4wire'}
    ledger=[];unmapped=[]
    for kind, files in result['classOperations'].items():
        files['migration']='neutral-contract-and-SCPI-implementation; facade/device-package-cutover-pending'
        for relative in files['sources']:
            text=read(relative);rust=relative.endswith('.rs');asynchronous='/async_' in relative or '/Async' in relative
            pattern = r'\bpub\s+(?:async\s+)?fn\s+(\w+)\s*\((.*?)\)\s*(?:->([^\{]+))?\{' if rust else r'\bpublic\s+(?:(?:static|async|virtual|override)\s+)*([\w<>,?\[\]]+)\s+(\w+)\s*\((.*?)\)'
            matches=[]
            for m in re.finditer(pattern,text,re.S):
                name=m[1] if rust else m[2];signature=re.sub(r'\s+',' ',m[0].rstrip('{').strip());matches.append((name,signature,m.start()))
            if not rust:
                for m in re.finditer(r'\bpublic\s+(?:static\s+)?([\w<>,?\[\]]+)\s+(\w+)\s*(?:=>|\{)',text):
                    if m[1] not in ['class','enum','struct','record','interface']:matches.append((m[2],m[0].strip(),m.start()))
                for m in re.finditer(r'\bpublic\s+(?:Async)?'+kind+r'\s*\((.*?)\)',text,re.S):matches.append(('new',re.sub(r'\s+',' ',m[0]),m.start()))
            for name,signature,offset in matches:
                normal=re.sub('[^a-z0-9]','',name.lower());normal=normal.removesuffix('async');normal=aliases.get(normal,normal)
                op=mapped.get((kind,normal)) or common.get(normal)
                disposition='implemented-neutral-contract' if op else 'removed-constructor-session-or-raw-escape' if normal in removed or normal=='new' else 'explicit-sense-mode-replaces-boolean' if normal=='senseenable' else 'typed-quantity-unit-replaces-session-state-property' if normal=='measurementunit' else 'UNMAPPED'
                if disposition=='UNMAPPED':unmapped.append(f'{relative}:{name}')
                ledger.append(dict(kind=kind,language='Rust' if rust else 'C#',mode='async' if asynchronous else 'sync',source=relative,line=text[:offset].count('\n')+1,symbol=name,signature=signature,sourceSha256=result['sourceSha256'][relative],targetOperation=op['id'] if op else 'DcPowerSupplySetSenseMode' if normal=='senseenable' else 'Quantity.Unit' if normal=='measurementunit' else None,parameters=op['parameters'] if op else [],unit=op['unit'] if op else None,result=op['result'] if op else None,disposition=disposition,verification='shared typed operation/traffic/value cases' if op else 'intentional breaking API change; no constructor compatibility requirement',deviceImplementation='external vendor package required for final cutover'))
    if unmapped:raise ValueError('Unmapped public legacy operations: '+', '.join(unmapped))
    result['operationLedger']=ledger
    profiles=tomllib.loads(read('crates/instrument-core/data/dialects/profiles.toml'))['profile']
    repository_config=json.loads(read('spec/device-repositories.json'))
    result['dialectProfiles']=[dict(profile=p,owner='synthetic-validation-only' if p['id'].startswith('ci_') else 'standards-provenance-review-required' if p['id'].startswith('generic_') else repository_config['profileOwners'][p['id']],hardwareValidated=False) for p in profiles]
    result['repositorySetup']=repository_config
    result['genericOperationMap']=json.loads(read('spec/generic-scpi-map.json'))
    result['openTapOperations']=json.loads(read('spec/opentap-operations.json'))
    result['scpiCommandTables']=tomllib.loads(read('crates/instrument-core/data/scpi_commands.toml'))
    result['probeTables']=tomllib.loads(read('crates/instrument-core/data/capability_probes.toml'))
    # Reconcile public OpenTAP operation names to the new operation ledger.
    tap=[]
    for row in result['openTapOperations']['operations']:
        method=row.get('invoke',{}).get('method')
        normal=re.sub('[^a-z0-9]','',(method or '').lower());normal=aliases.get(normal,normal)
        op=mapped.get((row['instrumentKind'],normal))
        if row['id']=='identity_query':op=common['queryidentity']
        if row['id']=='safe_shutdown':op=common['shutdown']
        targets=[]
        if op:targets=[op['id']]
        elif row.get('composite'):
            for call in row['composite']:
                name=aliases.get(re.sub('[^a-z0-9]','',call['method'].lower()),re.sub('[^a-z0-9]','',call['method'].lower()))
                target=mapped.get((row['instrumentKind'],name))
                if target is None:raise ValueError('Unmapped composite call: '+call['method'])
                targets.append(target['id'])
        tap.append(dict(id=row['id'],stepTypeName=row['stepTypeName'],targetOperations=targets,sourceOperation=row,status='facade/OpenTAP composition pending external device packages',changedSemantics='explicit reset is separate from protocol-neutral shutdown' if row['id']=='safe_shutdown' else None))
    if any(not row['targetOperations'] for row in tap):raise ValueError('Unmapped OpenTAP operation: '+', '.join(row['id'] for row in tap if not row['targetOperations']))
    result['openTapLedger']=tap
    import fnmatch
    ordered=[p for p in profiles if not p['id'].startswith('generic_')]
    for item in result['deviceProfiles']:
        q=item['profile'];ordered.append(dict(id=q['id'],kind=q['kind'],manufacturer_glob=q['manufacturerGlob'],model_glob=q['modelGlob'],commands=q['commands']))
    ordered += [p for p in profiles if p['id'].startswith('generic_')]
    for model in result['modelHints']:
        model['legacyResolution']={}
        for kind in model['kinds']:
            selected=next((p for p in ordered if p['kind']==kind and fnmatch.fnmatchcase(model['manufacturer'].lower(),p['manufacturer_glob'].lower()) and fnmatch.fnmatchcase(model['model'].lower(),p['model_glob'].lower())),None)
            if selected is None:raise ValueError('No legacy profile for catalogued device/kind')
            model['legacyResolution'][kind]=dict(profile=selected['id'],commandKeys=sorted(selected['commands']),implicitFallbackMustBecomeExplicit=True,hardwareValidated=False)
    result['implementedNeutralOperationIds']=[op['id'] for op in manifest['operations']]
    result['nonTableBehavior']=repository_config['behaviorMigration']
    result['extractionSources']={}
    for folder in ['crates/instrument-core/src','crates/instrument/src','dotnet/src/InstrumentComponents','fixtures']:
        for path in sorted((root/folder).rglob('*')):
            if path.is_file() and path.suffix in ['.rs','.cs','.json'] and not any(p in ['bin','obj'] for p in path.parts):
                rel=path.relative_to(root).as_posix();data=path.read_bytes();result['extractionSources'][rel]=hashlib.sha256(data).hexdigest()
    result['sourceSha256']=dict(sorted(result['sourceSha256'].items()))
    return result
