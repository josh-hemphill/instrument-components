#!/usr/bin/env python3
"""Mutation checks for preserving historical proof while reconciling additions."""
import contextlib
import io
from pathlib import Path
import runpy

ROOT=Path(__file__).resolve().parents[1]
namespace=runpy.run_path(str(ROOT/'tools/device-extraction-index.py'))
check=namespace['check'];globals_=check.__globals__;original=globals_['read']
index=original(ROOT/'spec/device-extraction-index.json')

def rejects(label,file,mutate):
    def changed(path):
        value=original(path)
        if path==ROOT/file:mutate(value)
        return value
    globals_['read']=changed
    try:
        with contextlib.redirect_stdout(io.StringIO()):check(index)
    except AssertionError:
        return
    finally:
        globals_['read']=original
    raise AssertionError('Mutation was not rejected: '+label)

with contextlib.redirect_stdout(io.StringIO()):check(index)
rejects('lost historical step','spec/opentap-operations.json',lambda value:value['operations'].pop(0))
rejects('unindexed new step','spec/opentap-operations.json',lambda value:value['operations'].append(dict(value['operations'][-1],id='unindexed_step')))
rejects('lost new operation','spec/driver-operations.json',lambda value:value['operations'].pop())
rejects('missing new oracle','validation/neutral/additional-opentap-oracle.json',lambda value:value['cases'].pop())

def remove_loaded_case(value):
    cell=next(c for c in value['cells'] if c['id']=='core-opentap:linux-x64:net8.0:neutral-additions')
    cell['caseIds'].remove('opentap:rf_receiver_power:loaded-nondefault')
rejects('missing loaded execution','spec/device-execution-evidence.json',remove_loaded_case)

def drift_source(value):
    cell=next(c for c in value['cells'] if c['id']=='core-opentap:linux-x64:net8.0:neutral-additions')
    cell['sourceFileSha256']['dotnet/src/InstrumentComponents.OpenTap/DeviceInstrument.cs']='0'*64
rejects('source drift despite old proof','spec/device-execution-evidence.json',drift_source)
print('PASS six audit mutations: historical retention, exact added scope, oracle, loaded execution and source integrity')
