#!/usr/bin/env python3
"""Export reproducible source-reference handoffs, never executable vendor packages."""
import argparse
from collections import Counter
import hashlib
import json
from pathlib import Path
import shutil
import subprocess
import sys

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT / 'tools/driver-tools/src'))
from instrument_driver_tools.inventory import inventory


def export(destination):
    destination = destination.resolve()
    if destination == ROOT or ROOT in destination.parents:
        raise ValueError('Export outside the checkout')
    if destination.exists() and any(destination.iterdir()):
        raise ValueError('Use an empty destination; never overwrite a handoff')
    current = inventory(ROOT)
    if current != json.loads((ROOT / 'spec/device-migration-inventory.json').read_text()):
        raise ValueError('Regenerate and review the migration inventory first')
    destination.mkdir(parents=True, exist_ok=True)
    def write(relative, data):
        target = destination / relative
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_text(json.dumps(data, indent=2, sort_keys=True, ensure_ascii=False) + '\n', encoding='utf-8')
    # Keep original behavior and transcripts for conversion review. This reference
    # tree must NEVER be added to a main SDK or a vendor package's runtime assets.
    sources = current['extractionSources'] | current['sourceSha256']
    for relative, expected in sorted(sources.items()):
        source = ROOT / relative
        if hashlib.sha256(source.read_bytes()).hexdigest() != expected:
            raise ValueError('Source changed during export: ' + relative)
        target = destination / 'reference-source' / relative
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(source, target)
    write(Path('inventory.json'), current)
    counts = Counter(row['repository'] for row in current['modelHints'])
    owner = current['repositorySetup']['proposedOwner']
    for repository, count in sorted(counts.items()):
        models = [m for m in current['modelHints'] if m['repository'] == repository]
        keys = {(m['manufacturer'], m['model']) for m in models}
        payload = dict(schemaVersion=1, repository=f'{owner}/{repository}',
                       status='NOT_CREATED; conversion and package implementation pending',
                       hardwareValidated=False, requiredTargets=current['requiredTargets'],
                       modelHints=models, usbHints=[u for u in current['usbHints'] if (u['manufacturer'], u['model']) in keys],
                       vendorJsonProfiles=[p for p in current['deviceProfiles'] if p['repository'] == repository],
                       vendorTomlProfiles=[p for p in current['dialectProfiles'] if p['owner'] == repository],
                       behaviorMigration=current['nonTableBehavior'],
                       genericReference='inventory.json: genericOperationMap, scpiCommandTables, probeTables; source evidence only, never implicit fallback',
                       operationReference='inventory.json: operationLedger, implementedNeutralOperationIds, openTapLedger',
                       sourceSha256=sources)
        write(Path(repository) / 'handoff.json', payload)
        (destination / repository / 'README.md').write_text(
            f'# {repository}\n\nRequired: all {count} source model hints and their class operations. '
            'Hints are not hardware evidence. This is an extraction input, not a driver package.\n\n'
            '1. Create the repository with the existing project license and configure Cargo/NuGet publishing.\n'
            '2. Convert the explicit vendor and applicable legacy generic templates to the released profile schema; '
            'preserve behavior using reference-source and inventory.json, resolving documented fallbacks explicitly.\n'
            '3. Add independently versioned Rust/.NET driver packages with static exported factories and supplied host IO. '
            'Use only published SDK dependencies, not paths into the main checkout.\n'
            '4. Port exact shared transcripts and add per-operation error, units, output, recovery and cancellation cases. '
            'Generate static Rust/C# profiles and tests with the checksummed released instrument-driver-tools wheel.\n'
            '5. Call driver-package.yml pinned to a reviewed SDK commit; require native Windows/Linux, net8/net10, '
            'default/async Rust and package-only AOT consumer execution before preview publication.\n'
            '6. Compose factories into facade/discovery/OpenTAP test applications, then remove this family from legacy '
            'core data and shared class branches. Record package versions and mark hardware claims unverified until bench evidence exists.\n', encoding='utf-8')
    (destination / 'README.md').write_text(
        '# Device package extraction handoff\n\n'
        'No repositories or packages were created by this export. All five vendor groups are required for initial cutover; '
        'a pilot orders the work and does not narrow release scope. reference-source preserves legacy behavior and fixtures '
        'for audit only. It is not a dependency or production resource bundle.\n\n'
        'See docs/device-driver-sdk.md and docs/device-package-delivery.md in the main repository for SDK boundaries, '
        'setup requirements and acceptance gates. inventory.json reconciles all 47 hints, one USB hint, eight classes, '
        'all profiles and public operation mappings. Existing generic traffic is source evidence, not universal standards support.\n', encoding='utf-8')
    files = {str(p.relative_to(destination)).replace('\\', '/'): hashlib.sha256(p.read_bytes()).hexdigest()
             for p in sorted(destination.rglob('*')) if p.is_file()}
    write(Path('manifest.json'), dict(schemaVersion=1, sourceHead=subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=ROOT, text=True).strip(),
                                    sourceFiles=sources, fileSha256=files, modelCounts=dict(sorted(counts.items())),
                                    note='HEAD plus file hashes identifies an uncommitted snapshot; no hardware evidence'))
    print(f'Exported {sum(counts.values())} model hints to {len(counts)} repository handoffs: {destination}')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    try:
        export(args.output)
    except (OSError, ValueError) as error:
        parser.exit(1, str(error) + '\n')
