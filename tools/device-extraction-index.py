#!/usr/bin/env python3
"""Reconcile external source/case ownership, or check the committed core-only index.

Only --write needs external checkouts. Normal core CI verifies the committed
index without fetching, building or loading any external device implementation.
"""
import argparse
import hashlib
import json
from pathlib import Path

ROOT=Path(__file__).resolve().parents[1]
VENDORS=['keysight','keithley','rigol','siglent','tektronix']
def read(path):return json.loads(path.read_text(encoding='utf-8'))
def digest(path):return hashlib.sha256(path.read_bytes()).hexdigest()
def source_digest(path):return hashlib.sha256(path.read_bytes().replace(b"\r\n",b"\n")).hexdigest()
def write(path,value):path.write_text(json.dumps(value,indent=2,sort_keys=True)+'\n',encoding='utf-8')
def generate(parent):
    archive=parent/'instrument-components-keysight/provenance/migration-tooling/device-migration-inventory.json'
    baseline=read(archive);models=[];packages=[];coverage=[]
    for vendor in VENDORS:
        repo='instrument-components-'+vendor;root=parent/repo;profiles=read(root/'profiles.json')['profiles'];cases=read(root/'cases.json')['cases'];identities=read(root/'provenance/identities.json')
        records=[]
        for profile in profiles:
            positive={call['operation'] for case in cases if case['id'].split('|')[0]==profile['id'] for i,call in enumerate(case['calls']) if i+1<len(case['calls']) or 'expectedError' not in case}
            declared={p['operation'] for p in profile['plans']}
            for operation in sorted(positive|declared):
                matching=[case for case in cases if case['id'].split('|')[0]==profile['id'] and any(call['operation']==operation for call in case['calls'])]
                if not matching:raise ValueError('No executable case: '+profile['id']+'/'+operation)
                records.append(dict(driver=profile['id'],operation=operation,cases=[c['id'] for c in matching],negativeCases=[c['id'] for c in matching if 'expectedError' in c and c['calls'][-1]['operation']==operation],disposition='implemented-source-derived-no-hardware-evidence' if operation in declared else 'explicit-inventory-binding-required'))
        write(root/'provenance/operation-coverage.json',records)
        for identity in identities:
            for hint in identity['hints']:
                models.append(dict(manufacturer=hint['manufacturer'],model=hint['model'],kinds=hint['kinds'],repository=repo,driver=identity['driver'],legacyResolvedProfiles={kind:value['profile'] for kind,value in hint['legacyResolution'].items()},modelSourceSha256=baseline['sourceSha256']['crates/instrument-core/data/model_registry.toml'],evidence='source-hint-and-software-transcripts-only',hardwareValidated=False))
        files=['profiles.json','cases.json','driver-package.json','provenance/identities.json','provenance/operation-coverage.json','provenance/source.json','provenance/public-symbols.json']
        packages.append(dict(repository='josh-hemphill/'+repo,rustPackage='instrument-driver-'+vendor,nugetPackage='InstrumentComponents.Drivers.'+vendor.title(),version='0.2.0-alpha.1',factoryCount=len(profiles),caseCount=len(cases),files={file:digest(root/file) for file in files},validation=dict(workflow='ci.yml',matrix=dict(os=['ubuntu-latest','windows-latest'],framework=['net8.0','net10.0']),status='execution-evidence-recorded-separately-by-commit-and-artifact-hash')))
        coverage.extend(dict(row,repository=repo) for row in records)
    canonical={(m['manufacturer'],m['model']):m for m in models}
    if {(m['manufacturer'],m['model']) for m in baseline['modelHints']}!=set(canonical):raise ValueError('Model hint loss')
    symbols=baseline['operationLedger']
    for symbol in symbols:
        symbol['deviceImplementation']='explicit external package registration and one shared protocol-neutral handle'
        symbol['verification']='removed by intentional breaking cutover' if symbol['targetOperation'] is None else 'typed neutral catalog plus external operation cases indexed below'
    profiles=[]
    for p in baseline['dialectProfiles']:
        ident=p['profile']['id'];owner=p['owner']
        profiles.append(dict(id=ident,source='TOML',disposition='development-only-synthetic' if ident.startswith('ci_') else 'explicit-external-source-derived-plans',owner=owner if owner!='standards-provenance-review-required' else 'applicable external device packages',sourceSha256=baseline['sourceSha256']['crates/instrument-core/data/dialects/profiles.toml']))
    for p in baseline['deviceProfiles']:profiles.append(dict(id=p['profile']['id'],source='vendorJSON',disposition='explicit-external-source-derived-plans',owner=p['repository']))
    steps=[];current={o['id']:o for o in read(ROOT/'spec/opentap-operations.json')['operations']}
    for row in baseline['openTapLedger']:
        if row['id'] not in current:raise ValueError('OpenTAP step lost')
        steps.append(dict(id=row['id'],stepTypeName=current[row['id']]['stepTypeName'],targetOperations=row['targetOperations'],changedSemantics=row['changedSemantics'],test='dotnet/tests/InstrumentComponents.OpenTap.Tests/OperationExecutionTests.cs',disposition='implemented-on-protocol-neutral-handle'))
    evidencePath=ROOT/'spec/device-execution-evidence.json'
    evidence=read(evidencePath) if evidencePath.exists() else dict(cells=[],corpora={})
    cells={cell['id']:cell for cell in evidence['cells']}
    for package in packages:
        vendor=package['repository'].rsplit('-',1)[1];corpusId='driver:'+vendor
        corpus=evidence['corpora'].get(corpusId)
        if corpus and corpus['caseManifestSha256']!=package['files']['cases.json']:raise ValueError('Execution corpus differs from source cases: '+vendor)
        package['executionCellIds']=sorted(cell['id'] for cell in cells.values() if cell.get('corpusId')==corpusId and cell['kind']=='case-execution')
        package['caseCorpusId']=corpusId
    packageByRepo={package['repository'].split('/')[1]:package for package in packages}
    for record in coverage:
        package=packageByRepo[record['repository']];record['id']=record['driver']+'/'+record['operation'];record['executionCellIds']=package['executionCellIds']
        corpus=evidence['corpora'].get(package['caseCorpusId'])
        if corpus and not set(record['cases'])<=set(corpus['caseIds']):raise ValueError('Case not in executed corpus')
    kinds={model['driver']:set().union(*(set(m['kinds']) for m in models if m['driver']==model['driver'])) for model in models}
    for symbol in symbols:
        operation=symbol['targetOperation']
        related=[row for row in coverage if symbol['kind'] in kinds[row['driver']] and (operation=='Quantity.Unit' or row['operation']==operation)] if operation else []
        symbol['operationCoverageIds']=[row['id'] for row in related]
        symbol['executionCellIds']=sorted({cell for row in related for cell in row['executionCellIds']})
    for model in models:model['executionCellIds']=packageByRepo[model['repository']]['executionCellIds']
    for step in steps:
        step['test']='dotnet/tests/InstrumentComponents.OpenTap.Tests/OperationOracleTests.cs'
        step['testCaseIds']=['opentap:'+step['id']+':nondefault','opentap:'+step['id']+':loaded-nondefault']
        step['oracle']='validation/neutral/opentap-oracle.json'
        step['oracleSha256']=digest(ROOT/step['oracle']) if (ROOT/step['oracle']).exists() else None
        step['executionCellIds']=sorted(cell['id'] for cell in cells.values() if cell['kind']=='opentap-exact-and-loaded')
        if current[step['id']]['publish']['mode'] in ['sample','sampleAndScalar']:step['changedSemantics']=(step['changedSemantics'] or '')+' Sample rows additionally preserve the actual physical Unit.'
    return dict(executionEvidenceFile='spec/device-execution-evidence.json',executionEvidenceSha256=digest(evidencePath) if evidencePath.exists() else None,schemaVersion=1,baselineCommit='5eb92b17f775d326e5a3542ee40855b156321745',hardwareValidated=False,apiCompatibilityRequired=False,publicationStatus='not-published-by-this-migration',sourceHashes=baseline['sourceSha256']|baseline['extractionSources'],models=models,usbHints=[dict(row,repository=canonical[(row['manufacturer'],row['model'])]['repository'],driver=canonical[(row['manufacturer'],row['model'])]['driver']) for row in baseline['usbHints']],profiles=profiles,operations=baseline['implementedNeutralOperationIds'],publicSymbols=symbols,openTapSteps=steps,packages=packages,operationCoverage=coverage)

def check(index,parent=None):
    assert len(index['models'])==47 and sum(len(m['kinds']) for m in index['models'])==48
    assert len(index['usbHints'])==1 and len(index['profiles'])==15
    assert sum(p['disposition']=='development-only-synthetic' for p in index['profiles'])==2
    assert len(index['publicSymbols'])==458 and len(index['openTapSteps'])==74
    operations={o['id'] for o in read(ROOT/'spec/driver-operations.json')['operations']}
    assert len(index['operations'])==83 and set(index['operations'])<=operations
    assert {r['id'] for r in index['openTapSteps']}=={r['id'] for r in read(ROOT/'spec/opentap-operations.json')['operations']}
    assert all(row['targetOperation'] is None or row['targetOperation'] in operations|{'Quantity.Unit'} for row in index['publicSymbols'])
    assert len({(m['manufacturer'],m['model']) for m in index['models']})==47
    assert {row['operation'] for row in index['operationCoverage']}==set(index['operations']),'Operation coverage does not reconcile all83 operations'
    assert len(index['packages'])==5 and all(r['cases'] for r in index['operationCoverage'])
    evidence=read(ROOT/index['executionEvidenceFile'])
    assert digest(ROOT/index['executionEvidenceFile'])==index['executionEvidenceSha256'],'Execution index drift'
    cells={cell['id']:cell for cell in evidence['cells']};assert len(cells)==len(evidence['cells'])
    for cell in cells.values():
        assert digest(ROOT/cell['log'])==cell['logSha256'],'Executed log drift: '+cell['id']
    for row in index['operationCoverage']:
        assert row['executionCellIds'],'Missing execution join: '+row['id']
        for identity in row['executionCellIds']:
            cell=cells[identity];assert cell['status']=='PASS' and cell['kind']=='case-execution'
            corpus=evidence['corpora'][cell['corpusId']];assert set(row['cases'])<=set(corpus['caseIds'])
    for package in index['packages']:
        selected=[cells[identity] for identity in package['executionCellIds']]
        assert {(cell['rid'],cell['framework']) for cell in selected}=={(rid,framework) for rid in ['linux-x64','win-x64'] for framework in ['net8.0','net10.0']}
        assert evidence['corpora'][package['caseCorpusId']]['caseManifestSha256']==package['files']['cases.json']
    assert all(row['targetOperation'] is None or row['executionCellIds'] for row in index['publicSymbols'])
    for row in index['openTapSteps']:
        assert row['executionCellIds'] and digest(ROOT/row['oracle'])==row['oracleSha256'],'Missing/current OpenTAP evidence'
        for identity in row['executionCellIds']:
            cell=cells[identity];assert cell['kind']=='opentap-exact-and-loaded' and cell['status']=='PASS'
            assert set(row['testCaseIds'])<=set(cell['caseIds']) and cell['oracleSha256']==row['oracleSha256']
            for file,expected in cell['sourceFileSha256'].items():assert source_digest(ROOT/file)==expected,'Tested OpenTAP source drift: '+file

    # Production dependencies/resources never pull implementation packages or old
    # model/dialect tables into the standalone main build.
    forbidden=['crates/instrument-core/data/model_registry.toml','crates/instrument-core/data/dialects/profiles.toml','crates/instrument-core/data/scpi_commands.toml','dotnet/src/InstrumentComponents/Dialects','dotnet/src/InstrumentComponents/Classes']
    assert not any((ROOT/file).exists() for file in forbidden),'Legacy implementation remains in core'
    for directory in [ROOT/'crates',ROOT/'dotnet/src']:
        for path in directory.rglob('*'):
            if path.name not in ['Cargo.toml'] and path.suffix!='.csproj':continue
            text=path.read_text(encoding='utf-8');assert 'instrument-driver-' not in text and 'InstrumentComponents.Drivers.' not in text,'External production dependency: '+str(path)
    if parent:
        for package in index['packages']:
            root=parent/package['repository'].split('/')[1]
            for file,expected in package['files'].items():assert digest(root/file)==expected,'External source drift: '+str(root/file)
    print('PASS extraction index:47 hints+USB,48 model-kind rows,13TOML+2JSON,83ops,458symbols,74steps; standalone main ownership')
if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__);parser.add_argument('--write',action='store_true');parser.add_argument('--external-root',type=Path);args=parser.parse_args();path=ROOT/'spec/device-extraction-index.json'
    if args.write:write(path,generate(args.external_root or ROOT.parent))
    check(read(path),args.external_root)
