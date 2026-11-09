import contextlib
import copy
import hashlib
import io
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
import runpy

ROOT=Path(__file__).resolve().parents[3]
sys.path.insert(0,str(ROOT/'tools/driver-tools/src'))
from instrument_driver_tools.cli import main, read_json
from instrument_driver_tools.profiles import validate,emit,catalogue
from instrument_driver_tools.inventory import inventory


class ToolingTests(unittest.TestCase):
    def setUp(self):
        self.profiles=json.loads((ROOT/'validation/scpi/profiles.json').read_text(encoding='utf-8'))
    def test_preservation_inventory_reconciles_authoritative_sources(self):
        data=inventory(ROOT)
        self.assertEqual(len(data['modelHints']),47);self.assertEqual(len(data['usbHints']),1)
        self.assertEqual(len(data['classOperations']),8);self.assertEqual(len(data['dialectProfiles']),13)
        self.assertEqual(data,json.loads((ROOT/'spec/device-migration-inventory.json').read_text(encoding='utf-8')))
        self.assertTrue(all(row['disposition']!='UNMAPPED' for row in data['operationLedger']))
        self.assertFalse(any(row['hardwareValidated'] for row in data['modelHints']))
        self.assertTrue({'keysight_sensor','keysight_xseries','rigol_dsa'}.issubset({p['profile']['id'] for p in data['dialectProfiles']}))
    def test_bundle_has_semantic_contract_without_private_source_paths(self):
        source=json.loads((ROOT/'spec/driver-operations.json').read_text(encoding='utf-8'))
        source['operations']=[{k:v for k,v in op.items() if k!='source'} for op in source['operations']]
        self.assertEqual(catalogue(),source)
        self.assertNotIn('crates/',json.dumps(catalogue()))
    def test_profile_order_and_mapping_order_do_not_affect_emission(self):
        validate(self.profiles);first=emit(self.profiles)
        alternate=copy.deepcopy(self.profiles);alternate['profiles'][0]['plans'].reverse()
        for plan in alternate['profiles'][0]['plans']:
            plan['tokens']=dict(reversed(list(plan['tokens'].items())))
        self.assertEqual(first,emit(alternate))
    def test_invalid_manifest_profiles_fail_before_writing_any_files(self):
        def wrong_version(d):d['contractMajor']=999
        def extra(d):d['unknown']=True
        def duplicate(d):d['profiles'].append(copy.deepcopy(d['profiles'][0]))
        def duplicate_op(d):d['profiles'][0]['plans'].append(copy.deepcopy(d['profiles'][0]['plans'][0]))
        def channel(d):d['profiles'][0]['channels']=[1,1]
        def unknown_output(d):d['profiles'][0]['outputs']=[999]
        def unknown_op(d):d['profiles'][0]['plans'][0]['operation']='Unknown'
        def malformed(d):d['profiles'][0]['plans'][0]['steps'][0]['command']='BAD? {range:bad}'
        def ignored(d):next(p for p in d['profiles'][0]['plans'] if p['operation']=='DcPowerSupplySetVoltage')['steps'][0]['command']='VOLT {volts}'
        def injection(d):d['profiles'][0]['plans'][0]['steps'][0]['command']='BAD?\n*RST'
        def token(d):next(p for p in d['profiles'][0]['plans'] if p['operation']=='DcPowerSupplyOutputEnable')['tokens']['enabled']['true']='ON;*RST'
        def role(d):d['profiles'][0]['plans'][0]['steps'][0]['response']='Write'
        def unknown_field(d):d['profiles'][0]['plans'][0]['steps'][0]['unknown']='x'
        def nonascii(d):d['profiles'][0]['plans'][0]['steps'][0]['command']='é?'
        def unknown_outputs(d):d['profiles'][0]['outputs']=None
        def late_preamble(d):next(p for p in d['profiles'][0]['plans'] if p['operation']=='OscilloscopeCaptureVoltageTrace')['steps'].reverse()
        for mutate in [wrong_version,extra,duplicate,duplicate_op,channel,unknown_output,unknown_op,malformed,ignored,injection,token,role,unknown_field,nonascii,unknown_outputs,late_preamble]:
            with self.subTest(mutate=mutate.__name__),tempfile.TemporaryDirectory() as temp:
                data=copy.deepcopy(self.profiles);mutate(data);folder=Path(temp);inp=folder/'input.json';inp.write_text(json.dumps(data));out=folder/'out'
                with contextlib.redirect_stderr(io.StringIO()),self.assertRaises(SystemExit):main(['generate','--input',str(inp),'--output-dir',str(out),'--contract-major','1'])
                self.assertFalse(out.exists())
    def test_check_is_read_only_and_detects_drift(self):
        with tempfile.TemporaryDirectory() as temp:
            folder=Path(temp);inp=folder/'input.json';inp.write_text(json.dumps(self.profiles));out=folder/'out'
            command=['generate','--input',str(inp),'--output-dir',str(out),'--contract-major','1']
            main(command);before={p.name:hashlib.sha256(p.read_bytes()).hexdigest() for p in out.iterdir()};main(command+['--check']);self.assertEqual(before,{p.name:hashlib.sha256(p.read_bytes()).hexdigest() for p in out.iterdir()})
            target=out/'profiles.rs';target.write_text(target.read_text(encoding='utf-8')+'// drift\n')
            with contextlib.redirect_stderr(io.StringIO()),self.assertRaises(SystemExit):main(command+['--check'])
    def test_codegen_rejects_non_generated_file_and_invalid_namespace_or_module(self):
        for module,namespace in [('bad/../../path','Good'),('good','A;System.Console.WriteLine'),('self','Good'),('good','class'),('match','Good'),('good','interface')]:
            with self.subTest(module=module),self.assertRaises(ValueError):emit(self.profiles,module,namespace)
        with tempfile.TemporaryDirectory() as temp:
            folder=Path(temp);inp=folder/'input.json';inp.write_text(json.dumps(self.profiles));out=folder/'out';out.mkdir();(out/'profiles.rs').write_text('hand-written code')
            with contextlib.redirect_stderr(io.StringIO()),self.assertRaises(SystemExit):main(['generate','--input',str(inp),'--output-dir',str(out),'--contract-major','1'])
            self.assertEqual((out/'profiles.rs').read_text(encoding='utf-8'),'hand-written code');self.assertFalse((out/'GeneratedProfiles.cs').exists())
    def test_wrong_explicit_schema_rejected(self):
        with tempfile.TemporaryDirectory() as temp:
            path=Path(temp)/'schema.json';path.write_text('{}')
            with self.assertRaises(ValueError):validate(self.profiles,path)
    def test_json_and_scenario_types_reject_invalid_source_before_emission(self):
        from instrument_driver_tools.cases import emit_cases
        with tempfile.TemporaryDirectory() as temp:
            path=Path(temp)/'input.json'
            for raw in ['{"profiles":[],"profiles":[]}', '{"x":NaN}', '{"x":Infinity}']:
                path.write_text(raw)
                with self.assertRaises(ValueError):read_json(path)
        cases=json.loads((ROOT/'spec/driver-scpi-cases.json').read_text(encoding='utf-8'))
        for kind,value in [('count','1);Injected();'),('count',True),('boolean',1),('quantity',True)]:
            data=copy.deepcopy(cases);data['cases']=data['cases'][:1]
            expected={'kind':kind,'value':value}
            if kind=='quantity':expected['unit']='Volt'
            data['cases'][0].pop('expectedError',None);data['cases'][0]['expected']=expected
            with self.subTest(kind=kind,value=value),self.assertRaises(ValueError):emit_cases(data)
    def test_numeric_defaults_and_typed_outputs_are_consistent(self):
        for value in ['NaN','Infinity','1e999','0','-1']:
            data=copy.deepcopy(self.profiles)
            plan=next(p for p in data['profiles'][0]['plans'] if p['operation']=='DmmMeasureVoltageDc')
            plan.setdefault('defaults',{})['range']=value
            with self.subTest(value=value),self.assertRaises(ValueError):validate(data)
        data=copy.deepcopy(self.profiles);data['profiles'][0]['outputs']=[{'kind':'FunctionGenerator'}]
        with self.assertRaises(ValueError):validate(data)
    def test_burst_count_profile_needs_no_physical_channels(self):
        data=copy.deepcopy(self.profiles);profile=data['profiles'][0]
        profile['channels']=None;profile['outputs']=[]
        profile['plans']=[p for p in profile['plans'] if p['operation']=='FunctionGeneratorSetBurstCount']
        validate(data);self.assertIn('FunctionGeneratorSetBurstCount',emit(data)['profiles.rs'])
    def test_shared_semantic_domain_and_shutdown_cases_fail_before_emission(self):
        examples=json.loads((ROOT/'spec/driver-profile-token-cases.json').read_text(encoding='utf-8'))['cases']
        for example in examples:
            data=copy.deepcopy(self.profiles);profile=data['profiles'][0]
            plan=next(p for p in profile['plans'] if p['operation']==example['operation']);plan['tokens'][example['parameter']]=example['tokens']
            if not example['shutdown']:profile['plans']=[p for p in profile['plans'] if p['operation']!='Shutdown']
            with self.subTest(id=example['id']),tempfile.TemporaryDirectory() as temp:
                folder=Path(temp);inp=folder/'in.json';inp.write_text(json.dumps(data));out=folder/'out'
                command=['generate','--input',str(inp),'--output-dir',str(out),'--contract-major','1']
                if example['accept']:main(command);self.assertTrue((out/'profiles.rs').is_file())
                else:
                    with contextlib.redirect_stderr(io.StringIO()),self.assertRaises(SystemExit):main(command)
                    self.assertFalse(out.exists())
    def test_codegen_rejects_symlink_targets_without_writing_other_files(self):
        with tempfile.TemporaryDirectory() as temp:
            folder=Path(temp);inp=folder/'input.json';inp.write_text(json.dumps(self.profiles));out=folder/'out';out.mkdir()
            victim=folder/'handwritten';victim.write_text('keep')
            try:(out/'profiles.rs').symlink_to(victim)
            except OSError:self.skipTest('Host cannot create symlinks')
            with contextlib.redirect_stderr(io.StringIO()),self.assertRaises(SystemExit):main(['generate','--input',str(inp),'--output-dir',str(out),'--contract-major','1'])
            self.assertEqual(victim.read_text(encoding='utf-8'),'keep');self.assertFalse((out/'GeneratedProfiles.cs').exists())
    def test_preview_validator_distinguishes_legacy_version_and_rejects_stable_tags(self):
        command=[sys.executable,str(ROOT/'tools/validate-sdk-release.py')]
        self.assertEqual(subprocess.run(command+['--tag','driver-sdk-v0.2.0-alpha.1'],capture_output=True).returncode,0)
        for tag in ['v0.1.1','driver-sdk-v0.2.0','driver-sdk-v0.2.0-alpha.2']:
            self.assertNotEqual(subprocess.run(command+['--tag',tag],capture_output=True).returncode,0)
    def test_package_dependency_gate_rejects_minimum_and_caret_ranges(self):
        checks=runpy.run_path(str(ROOT/'tools/validate-driver-sdk.py'))
        for requirement in ['0.2.0-alpha.1','^0.2.0-alpha.1','>=0.2.0-alpha.1','[0.2.0-alpha.1,)']:
            with self.subTest(requirement=requirement):
                with self.assertRaises(ValueError):checks['check_rust_pins']({'package':{'version':'0.2.0-alpha.1'},'dependencies':{'instrument-api':{'version':requirement}}})
                with self.assertRaises(ValueError):checks['check_nuget_pins']({'InstrumentComponents.Abstractions':requirement})
        checks['check_rust_pins']({'package':{'version':'0.2.0-alpha.1'},'dependencies':{'instrument-api':{'version':'=0.2.0-alpha.1'}}})
        checks['check_nuget_pins']({'InstrumentComponents.Abstractions':'[0.2.0-alpha.1]'})

if __name__=='__main__':unittest.main()
