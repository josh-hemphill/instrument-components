/**
 * Validate spec/opentap-operations.json against the curated OpenTAP contract.
 */
const root = new URL("..", import.meta.url);
const schemaPath = new URL("spec/opentap-operations.schema.json", root);
const manifestPath = new URL("spec/opentap-operations.json", root);
const scpiMapPath = new URL("spec/generic-scpi-map.json", root);

const ID_PATTERN = /^[a-z][a-z0-9_]*$/;
const STEP_PATTERN = /^[A-Z][A-Za-z0-9]*Step$/;
const TYPE_PATTERN = /^[A-Z][A-Za-z0-9]*$/;
const VERSION_PATTERN = /^[0-9]+\.[0-9]+\.[0-9]+$/;

const KINDS = new Set([
  "Dmm",
  "DcPowerSupply",
  "FunctionGenerator",
  "Oscilloscope",
  "Switch",
  "Counter",
  "PowerMeter",
  "SpectrumAnalyzer",
  "Scpi",
]);

const KIND_TO_GENERIC: Record<string, string> = {
  Dmm: "generic_dmm",
  DcPowerSupply: "generic_dcpwr",
  FunctionGenerator: "generic_fgen",
  Oscilloscope: "generic_scope",
  Switch: "generic_switch",
  Counter: "generic_counter",
  PowerMeter: "generic_pwrmeter",
  SpectrumAnalyzer: "generic_specan",
};

const IMPLEMENTATIONS = new Set(["handwritten", "generated"]);
const OPERATION_KINDS = new Set(["invoke", "composite", "utility"]);
const PARAM_TYPES = new Set(["string", "int", "uint", "double", "bool", "nullableDouble", "enum"]);
const RETURN_TYPES = new Set(["void", "double", "bool", "trace"]);
const PUBLISH_MODES = new Set(["none", "identity", "sample", "scalar", "sampleAndScalar", "multiScalar"]);
const CAPABILITIES = new Set(["base", "extension"]);

const OPERATION_KEYS = new Set([
  "id",
  "stepTypeName",
  "implementation",
  "kind",
  "instrumentKind",
  "instrumentType",
  "accessor",
  "displayName",
  "displayGroups",
  "description",
  "capability",
  "since",
  "parameters",
  "invoke",
  "composite",
  "publish",
]);

function requireString(value: unknown, label: string): string {
  if (typeof value !== "string" || value.length === 0) {
    throw new Error(`${label} must be a non-empty string`);
  }
  return value;
}

function requireObject(value: unknown, label: string): Record<string, unknown> {
  if (value == null || typeof value !== "object" || Array.isArray(value)) {
    throw new Error(`${label} must be an object`);
  }
  return value as Record<string, unknown>;
}

function assertNoExtraKeys(raw: Record<string, unknown>, allowed: Set<string>, label: string) {
  for (const key of Object.keys(raw)) {
    if (!allowed.has(key)) {
      throw new Error(`${label}: unexpected property ${JSON.stringify(key)}`);
    }
  }
}

function assertInvoke(raw: Record<string, unknown>, label: string, dialectKeys: Set<string>) {
  assertNoExtraKeys(raw, new Set(["method", "returns", "dialectKey", "args"]), label);
  requireString(raw.method, `${label}.method`);
  if (raw.returns != null && !RETURN_TYPES.has(requireString(raw.returns, `${label}.returns`))) {
    throw new Error(`${label}.returns is not a supported return type`);
  }
  if (raw.dialectKey != null) {
    const key = requireString(raw.dialectKey, `${label}.dialectKey`);
    if (!dialectKeys.has(key)) {
      throw new Error(`${label}.dialectKey ${JSON.stringify(key)} is missing from spec/generic-scpi-map.json`);
    }
  }
  if (raw.args != null) {
    if (!Array.isArray(raw.args)) {
      throw new Error(`${label}.args must be an array`);
    }
    for (const [index, arg] of raw.args.entries()) {
      const entry = requireObject(arg, `${label}.args[${index}]`);
      assertNoExtraKeys(entry, new Set(["parameter"]), `${label}.args[${index}]`);
      requireString(entry.parameter, `${label}.args[${index}].parameter`);
    }
  }
}

function dialectKeysFor(kind: string, scpiMap: Record<string, Record<string, unknown>>): Set<string> {
  const profile = KIND_TO_GENERIC[kind];
  if (profile == null) return new Set();
  const commands = scpiMap[profile] ?? {};
  return new Set(Object.keys(commands));
}

function validateOperation(
  raw: Record<string, unknown>,
  index: number,
  scpiMap: Record<string, Record<string, unknown>>,
) {
  const label = `operations[${index}]`;
  assertNoExtraKeys(raw, OPERATION_KEYS, label);
  const id = requireString(raw.id, `${label}.id`);
  if (!ID_PATTERN.test(id)) throw new Error(`${label}.id ${JSON.stringify(id)} is invalid`);
  const stepTypeName = requireString(raw.stepTypeName, `${label}.stepTypeName`);
  if (!STEP_PATTERN.test(stepTypeName)) {
    throw new Error(`${label}.stepTypeName ${JSON.stringify(stepTypeName)} is invalid`);
  }
  const implementation = requireString(raw.implementation, `${label}.implementation`);
  if (!IMPLEMENTATIONS.has(implementation)) {
    throw new Error(`${label}.implementation is invalid`);
  }
  const kind = requireString(raw.kind, `${label}.kind`);
  if (!OPERATION_KINDS.has(kind)) throw new Error(`${label}.kind is invalid`);
  const instrumentKind = requireString(raw.instrumentKind, `${label}.instrumentKind`);
  if (!KINDS.has(instrumentKind)) throw new Error(`${label}.instrumentKind is invalid`);
  const instrumentType = requireString(raw.instrumentType, `${label}.instrumentType`);
  if (!TYPE_PATTERN.test(instrumentType)) throw new Error(`${label}.instrumentType is invalid`);
  requireString(raw.displayName, `${label}.displayName`);
  if (!Array.isArray(raw.displayGroups) || raw.displayGroups.length < 2) {
    throw new Error(`${label}.displayGroups must have at least two entries`);
  }
  if (raw.displayGroups[0] !== "Instrument Components") {
    throw new Error(`${label}.displayGroups[0] must be "Instrument Components"`);
  }
  if (raw.description != null) requireString(raw.description, `${label}.description`);
  if (raw.capability != null && !CAPABILITIES.has(requireString(raw.capability, `${label}.capability`))) {
    throw new Error(`${label}.capability is invalid`);
  }
  const since = requireString(raw.since, `${label}.since`);
  if (!VERSION_PATTERN.test(since)) throw new Error(`${label}.since is invalid`);

  const dialectKeys = dialectKeysFor(instrumentKind, scpiMap);
  if (kind === "invoke") {
    requireString(raw.accessor, `${label}.accessor`);
    assertInvoke(requireObject(raw.invoke, `${label}.invoke`), `${label}.invoke`, dialectKeys);
    if (raw.composite != null) throw new Error(`${label}: invoke operations cannot set composite`);
  } else if (kind === "composite") {
    requireString(raw.accessor, `${label}.accessor`);
    if (!Array.isArray(raw.composite) || raw.composite.length === 0) {
      throw new Error(`${label}.composite must be a non-empty array`);
    }
    for (const [invokeIndex, invoke] of raw.composite.entries()) {
      assertInvoke(
        requireObject(invoke, `${label}.composite[${invokeIndex}]`),
        `${label}.composite[${invokeIndex}]`,
        dialectKeys,
      );
    }
    if (raw.invoke != null) throw new Error(`${label}: composite operations cannot set invoke`);
  } else if (raw.invoke != null || raw.composite != null || raw.accessor != null) {
    throw new Error(`${label}: utility operations cannot set invoke, composite, or accessor`);
  }

  if (raw.parameters != null) {
    if (!Array.isArray(raw.parameters)) throw new Error(`${label}.parameters must be an array`);
    for (const [parameterIndex, parameter] of raw.parameters.entries()) {
      const entry = requireObject(parameter, `${label}.parameters[${parameterIndex}]`);
      assertNoExtraKeys(
        entry,
        new Set(["name", "displayName", "type", "enumType", "order", "unit", "default"]),
        `${label}.parameters[${parameterIndex}]`,
      );
      requireString(entry.name, `${label}.parameters[${parameterIndex}].name`);
      requireString(entry.displayName, `${label}.parameters[${parameterIndex}].displayName`);
      const type = requireString(entry.type, `${label}.parameters[${parameterIndex}].type`);
      if (!PARAM_TYPES.has(type)) throw new Error(`${label}.parameters[${parameterIndex}].type is invalid`);
      if (type === "enum") requireString(entry.enumType, `${label}.parameters[${parameterIndex}].enumType`);
      if (typeof entry.order !== "number" || !Number.isInteger(entry.order) || entry.order < 2) {
        throw new Error(`${label}.parameters[${parameterIndex}].order must be an integer >= 2`);
      }
    }
  }

  const publish = requireObject(raw.publish, `${label}.publish`);
  assertNoExtraKeys(publish, new Set(["mode", "unit", "supportsLimits"]), `${label}.publish`);
  const mode = requireString(publish.mode, `${label}.publish.mode`);
  if (!PUBLISH_MODES.has(mode)) throw new Error(`${label}.publish.mode is invalid`);
}

function main() {
  JSON.parse(Deno.readTextFileSync(schemaPath));
  const manifest = requireObject(JSON.parse(Deno.readTextFileSync(manifestPath)), "manifest");
  const scpiMap = requireObject(JSON.parse(Deno.readTextFileSync(scpiMapPath)), "generic-scpi-map") as Record<
    string,
    Record<string, unknown>
  >;
  assertNoExtraKeys(manifest, new Set(["$schema", "version", "operations"]), "manifest");
  if (manifest.version !== 1) throw new Error("manifest.version must be 1");
  if (!Array.isArray(manifest.operations) || manifest.operations.length === 0) {
    throw new Error("manifest.operations must be a non-empty array");
  }

  const ids = new Set<string>();
  const stepTypes = new Set<string>();
  for (const [index, operation] of manifest.operations.entries()) {
    const raw = requireObject(operation, `operations[${index}]`);
    validateOperation(raw, index, scpiMap);
    const id = raw.id as string;
    const stepTypeName = raw.stepTypeName as string;
    if (ids.has(id)) throw new Error(`duplicate operation id ${id}`);
    if (stepTypes.has(stepTypeName)) throw new Error(`duplicate stepTypeName ${stepTypeName}`);
    ids.add(id);
    stepTypes.add(stepTypeName);
  }
}

main();
