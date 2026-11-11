#!/usr/bin/env python3
"""Export the current migration index and documentation, without driver source."""
import argparse
import hashlib
import json
from pathlib import Path
import shutil
import subprocess
ROOT=Path(__file__).resolve().parents[1]
def export(destination):
    destination=destination.resolve()
    if destination==ROOT or ROOT in destination.parents or destination in ROOT.parents:raise ValueError('Export to a separate directory outside the checkout')
    if destination.exists() and any(destination.iterdir()):raise ValueError('Use an empty destination')
    subprocess.run(['python','tools/device-extraction-index.py'],cwd=ROOT,check=True)
    destination.mkdir(parents=True,exist_ok=True)
    files=['spec/device-extraction-index.json','spec/device-repositories.json','spec/driver-sdk-release.json','spec/publishers.json','docs/device-driver-sdk.md','docs/device-package-delivery.md','tools/publisher-setup/README.md']
    for file in files:
        target=destination/file;target.parent.mkdir(parents=True,exist_ok=True);shutil.copy2(ROOT/file,target)
    manifest=dict(schemaVersion=2,sourceHead=subprocess.check_output(['git','rev-parse','HEAD'],cwd=ROOT,text=True).strip(),fileSha256={file:hashlib.sha256((destination/file).read_bytes()).hexdigest() for file in files},note='HEAD plus content hashes; code/provenance live in linked external repositories. No hardware evidence or publication implied.')
    (destination/'manifest.json').write_text(json.dumps(manifest,indent=2)+'\n',encoding='utf-8')
    print('Exported current extraction index and delivery documentation: '+str(destination))
if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__);parser.add_argument('--output',type=Path,required=True);args=parser.parse_args();export(args.output)
