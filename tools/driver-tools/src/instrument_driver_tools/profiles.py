"""Closed profile grammar and deterministic reflection-free Rust/C# emission."""
import json
import re
import itertools
from importlib.resources import files
from . import CONTRACT_MAJOR


def catalogue():
    return json.loads(files('instrument_driver_tools').joinpath('schemas/catalog.json').read_text(encoding='utf-8'))


def placeholders(template):
    names = set()
    while '{' in template:
        head, template = template.split('{', 1)
        if '}' in head or '}' not in template:
            raise ValueError('Unmatched template brace')
        name, template = template.split('}', 1)
        if not re.fullmatch(r'[A-Za-z][A-Za-z0-9_]*', name):
            raise ValueError('Invalid placeholder')
        names.add(name)
    if '}' in template:
        raise ValueError('Unmatched template brace')
    return names


def validate(data, schema_path=None):
    import jsonschema
    bundled = json.loads(files('instrument_driver_tools').joinpath('schemas/profiles.schema.json').read_text(encoding='utf-8'))
    if schema_path is not None and json.loads(schema_path.read_text(encoding='utf-8')) != bundled:
        raise ValueError('Schema differs from this tool release; install the matching tool version')
    jsonschema.Draft202012Validator(bundled).validate(data)
    metadata = {op['id']: op for op in catalogue()['operations']}
    ids = set()
    for profile in data['profiles']:
        if profile['id'] in ids:
            raise ValueError('Duplicate profile id')
        ids.add(profile['id'])
        for rule in profile.get('rules', []):
            if 'vendorId' in rule:continue
            if not rule['manufacturer'].strip() or not rule['model'].strip() or any(ord(c)<32 or ord(c)==127 for c in rule['manufacturer']+rule['model']): raise ValueError('Empty match rule')
        channels = profile['channels']; outputs = profile['outputs']
        if outputs is not None and any(o['kind']=='DcPowerSupply' and o['channel'] not in (channels or []) for o in outputs):
            raise ValueError('Outputs must be a subset of known channels')
        plans = {}
        for plan in profile['plans']:
            name = plan['operation']; op = metadata[name]
            if name in plans:
                raise ValueError('Duplicate operation')
            plans[name] = plan
            params = {p['name']: p['type'] for p in op['parameters']}
            if plan.get('choices'):
                if plan['steps'] or plan.get('tokens') or plan.get('defaults') or plan.get('unit') is not None:raise ValueError('Choice plan also contains flat plan data')
                optional=[p['name'] for p in op['parameters'] if p['type']=='optional_number']
                if not optional or len(optional)>8 or len(plan['choices'])>16:raise ValueError('Invalid or excessive choices')
                for choice in plan['choices']:
                    if set(choice['presence'])-set(optional):raise ValueError('Presence key must name optional argument')
                    flat={k:v for k,v in choice.items() if k!='presence'};flat['operation']=name
                    validate(dict(schemaVersion=1,contractMajor=1,profiles=[dict(profile,plans=[flat])]))
                for values_mask in itertools.product([False,True],repeat=len(optional)):
                    mask=dict(zip(optional,values_mask));matches=[c for c in plan['choices'] if all(mask[k]==v for k,v in c['presence'].items())]
                    if len(matches)>1:raise ValueError('Overlapping optional choices')
                    if not matches:continue
                    choice=matches[0];used=set()
                    for step in choice['steps']:
                        if step.get('when') is not None and not mask[step['when']]:continue
                        for key in placeholders(step['command']):
                            if mask.get(key) is False and key not in choice.get('defaults',{}):raise ValueError('Absent optional placeholder lacks explicit default')
                            used.add(key)
                    if any((typ!='optional_number' or mask[key]) and key not in used for key,typ in params.items()):raise ValueError('Choice ignores supplied or required argument')
                continue
            declared=plan.get('unit')
            if declared is not None:
                allowed=['Celsius','Kelvin','Unknown'] if op['unit']=='Temperature' else [op['unit']]
                if op['result']!='quantity' or op['unit'] in ['ConfiguredDmm','ConfiguredPower'] or declared not in allowed:raise ValueError('Result unit declaration violates dimension')
            used = set(); values = 0; preambles = 0
            for step in plan['steps']:
                command = step['command']; role = step['response']
                if not command.strip() or any(ord(c) < 32 or ord(c) > 126 for c in command) or len(command.encode()) > 65536:
                    raise ValueError('Invalid message body')
                if command.count('?') != (0 if role == 'Write' else 1):
                    raise ValueError('Query response-role mismatch')
                when = step.get('when')
                if when is not None and (params.get(when) != 'optional_number' or role in ('Value', 'Preamble')):
                    raise ValueError('Invalid conditional step')
                names = placeholders(command)
                if names - params.keys():
                    raise ValueError('Unknown operation placeholder')
                used |= names
                if role == 'Preamble' and values:
                    raise ValueError('Preamble must precede data')
                values += role == 'Value'; preambles += role == 'Preamble'
            tokens = plan.get('tokens', {}); defaults = plan.get('defaults', {})
            for key, typ in params.items():
                if typ != 'optional_number' and key not in used:
                    raise ValueError('Required argument ignored by plan')
                if typ == 'channel' and not channels:
                    raise ValueError('Channel operation requires a nonempty explicit inventory')
                if typ in ('boolean', 'symbol', 'waveform', 'power_unit', 'sense_mode') and key not in tokens:
                    raise ValueError('Missing semantic token mapping')
            for key, mapping in tokens.items():
                if params.get(key) not in ('boolean', 'symbol', 'waveform', 'power_unit', 'sense_mode') or not mapping:
                    raise ValueError('Invalid token mapping')
                if any(not re.fullmatch(r'[A-Za-z0-9_.+-]{1,128}', x) for pair in mapping.items() for x in pair):
                    raise ValueError('Unsafe token mapping')
                typ=params[key];domain=['true','false'] if typ=='boolean' else catalogue()['enums'].get(typ)
                if domain is not None and set(mapping)-set(domain) or typ=='symbol' and any(not re.fullmatch('[A-Za-z0-9_]+', k) for k in mapping):
                    raise ValueError('Token key outside typed semantic domain')
            for key, value in defaults.items():
                if params.get(key) != 'optional_number' or not re.fullmatch(r'[A-Za-z0-9_.+-]{1,128}', value):
                    raise ValueError('Invalid optional default')
                if value not in ['AUTO','DEF','MIN','MAX']:
                    import math
                    number=float(value);constraint=next(p['constraint'] for p in op['parameters'] if p['name']==key)
                    if not math.isfinite(number) or constraint=='positive' and number<=0 or constraint=='nonnegative' and number<0 or constraint=='percent' and not 0<=number<=100: raise ValueError('Invalid numeric default')
            local = name == 'DcPowerSupplyChannelCount'; shutdown = name == 'Shutdown'
            if local and outputs is None:
                raise ValueError('Channel count requires inventory')
            if shutdown and outputs is None:
                raise ValueError('Shutdown requires complete output inventory')
            if local and plan['steps']: raise ValueError('Metadata channel count performs no IO')
            if name=='SpectrumAnalyzerWaitOpc' and not any(s['response']=='Completion' for s in plan['steps']): raise ValueError('Completion requires explicit completion query')
            if not local and not shutdown and not plan['steps']:
                raise ValueError('Empty operation plan')
            if op['result'] in ('quantity', 'boolean', 'samples', 'identity') and values != 1:
                raise ValueError('Exactly one value query required')
            if op['result'] == 'trace' and (values != 1 or preambles != 1):
                raise ValueError('Trace requires preamble/data')
            if op['result'] in ('unit', 'count') and values != 0:
                raise ValueError('Unexpected value query')
            if op['result'] != 'trace' and preambles != 0:
                raise ValueError('Unexpected preamble query')
        if outputs is not None:
            kinds={o['kind'] for o in outputs}
            if any(metadata[name]['kind']=='DcPowerSupply' and any(p['type']=='channel' for p in metadata[name]['parameters']) for name in plans) and 'DcPowerSupply' not in kinds:raise ValueError('PSU channel operations contradict complete output inventory')
            if 'FunctionGeneratorOutputEnable' in plans and 'FunctionGenerator' not in kinds:raise ValueError('Generator output operation contradicts complete inventory')
        if outputs is not None and 'Shutdown' in plans:
            for output in outputs:
                if output['kind']+'OutputEnable' not in plans: raise ValueError('Shutdown lacks declared output disable capability')
                if 'false' not in plans[output['kind']+'OutputEnable'].get('tokens',{}).get('enabled',{}): raise ValueError('Shutdown cannot prepare declared output disable')
            kinds={o['kind'] for o in outputs}
            for kind in ['DcPowerSupply','FunctionGenerator']:
                if kind+'OutputEnable' in plans and kind not in kinds: raise ValueError('Output capability cannot claim zero corresponding outputs')


def rust_strings(source):
    def quote(match):
        value = json.loads(match.group(0)); out = '"'
        for char in value:
            out += {'\\':'\\\\', '"':'\\"', '\n':'\\n', '\r':'\\r', '\t':'\\t'}.get(char, ('\\u{'+format(ord(char),'x')+'}') if ord(char)<32 or ord(char)==127 else char)
        return out+'"'
    return re.sub(r'"(?:\\.|[^"\\])*"', quote, source)


def quoted(text):
    # Rust and C# accept JSON escapes for ASCII; emit unicode literally (UTF-8 source).
    return json.dumps(text, ensure_ascii=False)


def rust_map(mapping, value):
    return 'BTreeMap::from(['+', '.join('('+quoted(k)+'.into(), '+value(v)+')' for k,v in sorted(mapping.items()))+'])'


def cs_map(mapping, typename, value):
    return 'new Dictionary<string, '+typename+'> { '+', '.join('['+quoted(k)+'] = '+value(v) for k,v in sorted(mapping.items()))+' }'


def emit_rust_steps(steps):
    return 'vec!['+', '.join('Step { command: '+quoted(s['command'])+'.into(), response: ResponseRole::'+s['response']+', when: '+('Some('+quoted(s['when'])+'.into())' if s.get('when') is not None else 'None')+' }' for s in steps)+']'


def emit_cs_steps(steps):
    return 'new Step[] { '+', '.join('new Step('+quoted(s['command'])+', ResponseRole.'+s['response']+', '+(quoted(s['when']) if s.get('when') is not None else 'null')+')' for s in steps)+' }'


def emit_rust_body(plan):
    return 'steps: '+emit_rust_steps(plan['steps'])+', tokens: '+rust_map(plan.get('tokens',{}),lambda m:rust_map(m,lambda v:quoted(v)+'.into()'))+', defaults: '+rust_map(plan.get('defaults',{}),lambda v:quoted(v)+'.into()')+', unit: '+('Some(instrument_api::catalog::Unit::'+plan['unit']+')' if plan.get('unit') else 'None')


def emit_rust_plan(plan):
    choices=', '.join('instrument_scpi::PlanChoice { presence: '+rust_map(c['presence'],lambda v:'true' if v else 'false')+', '+emit_rust_body(c)+' }' for c in plan.get('choices',[]))
    return 'Plan { operation: CatalogOperation::'+plan['operation']+', '+emit_rust_body(plan)+', choices: vec!['+choices+'] }'


def emit_cs_body(plan):
    return emit_cs_steps(plan['steps'])+', '+cs_map(plan.get('tokens',{}),'IReadOnlyDictionary<string, string>',lambda m:cs_map(m,'string',quoted))+', '+cs_map(plan.get('defaults',{}),'string',quoted)


def emit_cs_plan(plan):
    choices=', '.join('new PlanChoice('+cs_map(c['presence'],'bool',lambda v:'true' if v else 'false')+', '+emit_cs_body(c)+', Unit: '+('Unit.'+c['unit'] if c.get('unit') else 'null')+')' for c in plan.get('choices',[]))
    return 'new Plan(CatalogOperation.'+plan['operation']+', '+emit_cs_body(plan)+', Choices: new PlanChoice[] { '+choices+' }, Unit: '+('Unit.'+plan['unit'] if plan.get('unit') else 'null')+')'


def emit(data, module='generated_profiles', namespace='GeneratedDrivers'):
    # Reject both languages' keywords rather than emit source that fails later.
    reserved = set('Self self super crate mod type async await fn pub use namespace class new public internal static record struct abstract as base bool break byte case catch char checked const continue decimal default delegate do double else enum event explicit extern false finally fixed float for foreach goto if impl implicit in int interface is let lock long loop match move mut null object operator out override params private protected readonly ref return sbyte sealed short sizeof stackalloc string switch this throw trait true try typeof uint ulong unchecked unsafe unsized ushort using var virtual void volatile where while yield dyn union become box final macro priv typeof gen'.split())
    if not re.fullmatch(r'[A-Za-z_][A-Za-z0-9_]*', module) or module in reserved:
        raise ValueError('Invalid Rust module name')
    if not re.fullmatch(r'[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*)*', namespace) or any(p in reserved for p in namespace.split('.')):
        raise ValueError('Invalid C# namespace')
    rust = ['// @generated by instrument-driver; edit the input profile manifest.', f'pub mod {module} {{', 'use instrument_api::{catalog::CatalogOperation, Result};', 'use instrument_scpi::{Profile, Plan, Step, ResponseRole};', 'use std::collections::BTreeMap;', 'pub fn profiles() -> Result<Vec<Profile>> { Ok(vec![']
    cs = ['// <auto-generated by instrument-driver />', '#nullable enable', 'using InstrumentComponents.Drivers.Catalog;', 'using InstrumentComponents.Scpi;', f'namespace {namespace};', 'public static class GeneratedProfiles { public static IReadOnlyList<ScpiProfile> Create() => new ScpiProfile[] {']
    for profile in sorted(data['profiles'], key=lambda p:p['id']):
        rlist = lambda x: 'None' if x is None else 'Some(vec!['+', '.join(str(c) for c in x)+'])'
        clist = lambda x: 'null' if x is None else 'new uint[] { '+', '.join(str(c) for c in x)+' }'
        routputs = 'None' if profile['outputs'] is None else 'Some(vec!['+', '.join('instrument_scpi::Output::DcPowerSupply('+str(o['channel'])+')' if o['kind']=='DcPowerSupply' else 'instrument_scpi::Output::FunctionGenerator' for o in profile['outputs'])+'])'
        coutputs = 'null' if profile['outputs'] is None else 'new Output[] { '+', '.join('new Output(OutputKind.'+o['kind']+(', '+str(o['channel']) if o['kind']=='DcPowerSupply' else '')+')' for o in profile['outputs'])+' }'
        rust += ['Profile::new('+quoted(profile['id'])+', '+rlist(profile['channels'])+', '+routputs+', vec![']
        cs += ['new ScpiProfile('+quoted(profile['id'])+', '+clist(profile['channels'])+', '+coutputs+', new Plan[] {']
        for plan in sorted(profile['plans'], key=lambda p:p['operation']):
            rust += [emit_rust_plan(plan)+','];cs += [emit_cs_plan(plan)+',']
        rust += ['])?,'];cs += ['}),']
    rust += [']) }']
    cs += ['};']
    for mode in range(3):
        native = mode == 1
        combined = mode == 2
        rname = 'combined_factories' if combined else 'native_factories' if native else 'sync_factories'
        rfactory = 'instrument_scpi::AsyncScpiFactory' if native else 'instrument_scpi::ScpiFactory'
        rconnector = 'instrument_scpi::AsyncConnector' if native else 'instrument_scpi::Connector'
        if native or combined: rust += ['#[cfg(feature="async")]']
        rust += ['pub fn '+rname+'(connector: std::sync::Arc<dyn '+rconnector+'>'+(', native_connector: std::sync::Arc<dyn instrument_scpi::AsyncConnector>' if combined else '')+') -> Result<Vec<std::sync::Arc<dyn instrument_api::DriverFactory>>> { let mut profiles = profiles()?.into_iter(); Ok(vec![']
        cs += ['public static IReadOnlyList<InstrumentComponents.Drivers.IDriverFactory> '+('CombinedFactories(IScpiConnector' if combined else 'NativeFactories(IAsyncScpiConnector' if native else 'SyncFactories(IScpiConnector')+' connector'+(', IAsyncScpiConnector nativeConnector' if combined else '')+') { var profiles = Create(); return new InstrumentComponents.Drivers.IDriverFactory[] {']
        for index, profile in enumerate(sorted(data['profiles'], key=lambda p:p['id'])):
            scheme=profile.get('scheme','scpi');rrules=[];crules=[]
            for rule in profile.get('rules',[]):
                if 'vendorId' in rule:
                    rrules.append('instrument_api::MatchRule::ExactProduct { scheme: '+quoted(scheme)+'.into(), vendor_id: '+str(rule['vendorId'])+', product_id: '+str(rule['productId'])+' }')
                    crules.append('new InstrumentComponents.Drivers.ExactProductRule('+quoted(scheme)+', '+str(rule['vendorId'])+', '+str(rule['productId'])+')')
                elif rule.get('family',False):
                    rrules.append('instrument_api::MatchRule::Family { scheme: '+quoted(scheme)+'.into(), manufacturer: '+quoted(rule['manufacturer'])+'.into(), model_pattern: '+quoted(rule['model'])+'.into() }')
                    crules.append('new InstrumentComponents.Drivers.FamilyRule('+quoted(scheme)+', '+quoted(rule['manufacturer'])+', '+quoted(rule['model'])+')')
                else:
                    rrules.append('instrument_api::MatchRule::ExactIdentity { scheme: '+quoted(scheme)+'.into(), manufacturer: '+quoted(rule['manufacturer'])+'.into(), model: '+quoted(rule['model'])+'.into() }')
                    crules.append('new InstrumentComponents.Drivers.ExactIdentityRule('+quoted(scheme)+', '+quoted(rule['manufacturer'])+', '+quoted(rule['model'])+')')
            if not rrules: rrules=['instrument_api::MatchRule::StandardScpi { scheme: '+quoted(scheme)+'.into() }'];crules=['new InstrumentComponents.Drivers.StandardScpiRule('+quoted(scheme)+')']
            rust += ['std::sync::Arc::new('+rfactory+'::new(profiles.next().expect("profile count checked"), '+quoted(scheme)+', connector.clone()).with_rules(vec!['+', '.join(rrules)+'])'+('.with_native_connector(native_connector.clone())' if combined else '')+') as std::sync::Arc<dyn instrument_api::DriverFactory>,']
            cs += ['new '+('AsyncScpiFactory' if native else 'ScpiFactory')+'(profiles['+str(index)+'], '+quoted(scheme)+', connector, new InstrumentComponents.Drivers.MatchRule[] { '+', '.join(crules)+' }'+(', nativeConnector' if combined else '')+'),']
        rust += [']) }'];cs += ['}; }']
    rust += ['}'];cs += ['}']
    return {'profiles.rs':rust_strings('\n'.join(rust)+'\n'), 'GeneratedProfiles.cs':('\n'.join(cs)+'\n').replace('\u2028','\\u2028').replace('\u2029','\\u2029')}
