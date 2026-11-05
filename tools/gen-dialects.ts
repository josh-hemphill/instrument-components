import { readFileSync, readdirSync } from "node:fs";
import { mkdir, readFile, writeFile } from "node:fs/promises";
/**
 * Generate Rust + C# dialect profile tables from data/dialects/profiles.toml
 * and spec/vendors/*.json. Vendor JSON is inserted before catch-all generic_*.
 */
import { rustfmtGenerated } from "./rustfmt-generated.ts";

const root = new URL("..", import.meta.url);

interface Profile {
  id: string;
  kind: string;
  manufacturer_glob: string;
  model_glob: string;
  channels: number;
  commands: Record<string, string>;
}

const KNOWN_KINDS = new Set([
  "Dmm",
  "DcPowerSupply",
  "FunctionGenerator",
  "Oscilloscope",
  "Switch",
  "Counter",
  "PowerMeter",
  "SpectrumAnalyzer",
]);

function unescapeTomlString(value: string): string {
  if (!(value.startsWith('"') && value.endsWith('"'))) {
    return value;
  }
  return value.slice(1, -1).replace(/\\"/g, '"').replace(/\\\\/g, "\\");
}

function parseTomlProfiles(text: string): Profile[] {
  const profiles: Profile[] = [];
  let current: Partial<Profile> | null = null;
  let inCommands = false;

  for (const raw of text.split("\n")) {
    const line = raw.trim();
    if (!line || line.startsWith("#")) continue;
    if (line === "[[profile]]") {
      if (current?.id) profiles.push(current as Profile);
      current = { commands: {}, channels: 1 };
      inCommands = false;
      continue;
    }
    if (line === "[profile.commands]") {
      inCommands = true;
      continue;
    }
    if (line.startsWith("[")) {
      inCommands = false;
      continue;
    }
    const eq = line.indexOf("=");
    if (eq < 0 || !current) continue;
    const key = line.slice(0, eq).trim();
    const value = unescapeTomlString(line.slice(eq + 1).trim());
    if (inCommands) {
      current.commands![key] = value;
    } else if (key === "channels") {
      current.channels = Number(value);
    } else {
      (current as Record<string, unknown>)[key] = value;
    }
  }
  if (current?.id) profiles.push(current as Profile);
  return profiles;
}

function requireNonEmptyString(value: unknown, label: string): string {
  if (typeof value !== "string" || value.length === 0) {
    throw new Error(`${label} must be a non-empty string`);
  }
  return value;
}

const VENDOR_ID_PATTERN = /^[a-z][a-z0-9_]*$/;
const VENDOR_OBJECT_KEYS = new Set([
  "$schema",
  "id",
  "kind",
  "manufacturerGlob",
  "modelGlob",
  "channels",
  "notes",
  "commands",
]);

function assertVendorSchema(name: string, raw: Record<string, unknown>): void {
  for (const key of Object.keys(raw)) {
    if (!VENDOR_OBJECT_KEYS.has(key)) {
      throw new Error(`${name}: unexpected property ${JSON.stringify(key)} (vendors.schema.json additionalProperties: false)`);
    }
  }
  const id = requireNonEmptyString(raw.id, `${name}: id`);
  if (!VENDOR_ID_PATTERN.test(id)) {
    throw new Error(`${name}: id ${JSON.stringify(id)} must match ${VENDOR_ID_PATTERN}`);
  }
  const kind = requireNonEmptyString(raw.kind, `${name}: kind`);
  if (!KNOWN_KINDS.has(kind)) {
    throw new Error(`${name}: unknown kind ${JSON.stringify(kind)}`);
  }
  requireNonEmptyString(raw.manufacturerGlob, `${name}: manufacturerGlob`);
  requireNonEmptyString(raw.modelGlob, `${name}: modelGlob`);
  if (raw.notes != null) {
    requireNonEmptyString(raw.notes, `${name}: notes`);
  }
  const commandsRaw = raw.commands;
  if (commandsRaw == null || typeof commandsRaw !== "object" || Array.isArray(commandsRaw)) {
    throw new Error(`${name}: commands must be an object`);
  }
  const commandEntries = Object.entries(commandsRaw as Record<string, unknown>);
  if (commandEntries.length === 0) {
    throw new Error(`${name}: commands must not be empty`);
  }
  for (const [key, value] of commandEntries) {
    requireNonEmptyString(value, `${name}: commands.${key}`);
  }
  if (raw.channels != null) {
    if (typeof raw.channels !== "number" || !Number.isInteger(raw.channels) || raw.channels < 1) {
      throw new Error(`${name}: channels must be a positive integer`);
    }
  }
}

function loadVendorJsonProfiles(): Profile[] {
  const dir = new URL("spec/vendors/", root);
  const names: string[] = [];
  try {
    for (const entry of readdirSync(dir, { withFileTypes: true })) {
      if (entry.isFile() && entry.name.endsWith(".json")) {
        names.push(entry.name);
      }
    }
  } catch (err) {
    if ((err as NodeJS.ErrnoException).code === "ENOENT") return [];
    throw err;
  }
  names.sort();
  return names.map((name) => {
    const path = new URL(name, dir);
    const raw = JSON.parse(readFileSync(path, "utf8")) as Record<string, unknown>;
    assertVendorSchema(name, raw);
    const commands: Record<string, string> = {};
    for (const [key, value] of Object.entries(raw.commands as Record<string, unknown>)) {
      commands[key] = value as string;
    }
    return {
      id: raw.id as string,
      kind: raw.kind as string,
      manufacturer_glob: raw.manufacturerGlob as string,
      model_glob: raw.modelGlob as string,
      channels: typeof raw.channels === "number" ? raw.channels : 1,
      commands,
    };
  });
}

function mergeProfiles(toml: Profile[], json: Profile[]): Profile[] {
  const seen = new Set<string>();
  const out: Profile[] = [];
  const add = (list: Profile[]) => {
    for (const profile of list) {
      if (seen.has(profile.id)) {
        throw new Error(`duplicate dialect id: ${profile.id}`);
      }
      seen.add(profile.id);
      out.push(profile);
    }
  };
  add(toml.filter((p) => !p.id.startsWith("generic_")));
  add(json);
  add(toml.filter((p) => p.id.startsWith("generic_")));
  return out;
}

const tomlProfiles = parseTomlProfiles(
  await readFile(new URL("crates/instrument-core/data/dialects/profiles.toml", root), "utf8"),
);
const profiles = mergeProfiles(tomlProfiles, loadVendorJsonProfiles());

const scpiToml = await readFile(
  new URL("crates/instrument-core/data/scpi_commands.toml", root),
  "utf8",
);
const genericMap = JSON.parse(
  await readFile(new URL("spec/generic-scpi-map.json", root), "utf8"),
) as Record<string, Record<string, [string, string]>>;

function parseTomlTables(text: string): Record<string, Record<string, string>> {
  const tables: Record<string, Record<string, string>> = {};
  let current = "";
  for (const raw of text.split("\n")) {
    const line = raw.trim();
    if (!line || line.startsWith("#")) continue;
    if (line.startsWith("[") && line.endsWith("]")) {
      current = line.slice(1, -1);
      tables[current] ??= {};
      continue;
    }
    const eq = line.indexOf("=");
    if (eq < 0 || !current) continue;
    const key = line.slice(0, eq).trim();
    tables[current][key] = unescapeTomlString(line.slice(eq + 1).trim());
  }
  return tables;
}

const scpiTables = parseTomlTables(scpiToml);
const mismatches: string[] = [];
for (const profile of profiles.filter((p) => p.id.startsWith("generic_"))) {
  const mapping = genericMap[profile.id];
  if (!mapping) {
    mismatches.push(`spec/generic-scpi-map.json missing profile ${profile.id}`);
    continue;
  }
  for (const [key, cmd] of Object.entries(profile.commands)) {
    const loc = mapping[key];
    if (!loc) {
      mismatches.push(`${profile.id}.${key} missing from spec/generic-scpi-map.json`);
      continue;
    }
    const [section, field] = loc;
    const expected = scpiTables[section]?.[field];
    if (expected !== cmd) {
      mismatches.push(
        `${profile.id}.${key}: dialect ${JSON.stringify(cmd)} != ${section}.${field} ${JSON.stringify(expected)}`,
      );
    }
  }
  for (const key of Object.keys(mapping)) {
    if (!(key in profile.commands)) {
      mismatches.push(`spec/generic-scpi-map.json has ${profile.id}.${key} but the dialect profile does not`);
    }
  }
}
if (mismatches.length > 0) {
  console.error("generic dialect profiles must match scpi_commands.toml:");
  for (const line of mismatches) console.error(`  ${line}`);
  process.exit(1);
}

function esc(s: string): string {
  return s.replace(/\\/g, "\\\\").replace(/"/g, '\\"');
}

function kindRust(kind: string): string {
  return `InstrumentKind::${kind}`;
}

function kindCs(kind: string): string {
  return `InstrumentKind.${kind}`;
}

let rust = `// @generated by tools/gen-dialects.ts — do not edit by hand
use crate::kind::InstrumentKind;
use std::collections::HashMap;

#[derive(Debug, Clone)]
pub struct DialectProfile {
    pub id: &'static str,
    pub kind: InstrumentKind,
    pub manufacturer_glob: &'static str,
    pub model_glob: &'static str,
    pub channels: u32,
    pub commands: &'static [(&'static str, &'static str)],
}

pub static DIALECT_PROFILES: &[DialectProfile] = &[
`;

for (const p of profiles) {
  const cmds = Object.entries(p.commands)
    .map(([k, v]) => `            ("${esc(k)}", "${esc(v)}")`)
    .join(",\n") + ",";
  rust += `    DialectProfile {
        id: "${esc(p.id)}",
        kind: ${kindRust(p.kind)},
        manufacturer_glob: "${esc(p.manufacturer_glob)}",
        model_glob: "${esc(p.model_glob)}",
        channels: ${p.channels},
        commands: &[
${cmds}
        ],
    },
`;
}
rust += `];

fn glob_match(pat: &str, value: &str) -> bool {
    let value = value.to_ascii_lowercase();
    let pat = pat.to_ascii_lowercase();
    if pat == "*" {
        return true;
    }
    let starts = pat.starts_with('*');
    let ends = pat.ends_with('*');
    match (starts, ends) {
        (true, true) => {
            let inner = &pat[1..pat.len() - 1];
            inner.is_empty() || value.contains(inner)
        }
        (false, true) => value.starts_with(&pat[..pat.len() - 1]),
        (true, false) => value.ends_with(&pat[1..]),
        (false, false) => value == pat,
    }
}

/// Resolves the first dialect profile matching kind + optional IDN fields.
pub fn resolve_dialect(
    kind: InstrumentKind,
    manufacturer: Option<&str>,
    model: Option<&str>,
) -> &'static DialectProfile {
    let mfr = manufacturer.unwrap_or("");
    let model = model.unwrap_or("");
    for profile in DIALECT_PROFILES {
        if profile.kind != kind {
            continue;
        }
        if glob_match(profile.manufacturer_glob, mfr) && glob_match(profile.model_glob, model) {
            return profile;
        }
    }
    DIALECT_PROFILES
        .iter()
        .find(|p| p.kind == kind && p.id.starts_with("generic_"))
        .or_else(|| DIALECT_PROFILES.iter().find(|p| p.kind == kind))
        .expect("no dialect profile for kind")
}

impl DialectProfile {
    pub fn command(&self, key: &str) -> Option<&'static str> {
        self.commands
            .iter()
            .find(|(k, _)| *k == key)
            .map(|(_, v)| *v)
    }

    pub fn command_map(&self) -> HashMap<&'static str, &'static str> {
        self.commands.iter().copied().collect()
    }

    /// Formats a command template replacing \`{name}\` placeholders.
    pub fn format_command(&self, key: &str, vars: &[(&str, String)]) -> Option<String> {
        let mut tmpl = self.command(key)?.to_string();
        for (name, value) in vars {
            tmpl = tmpl.replace(&format!("{{{name}}}"), value);
        }
        Some(tmpl)
    }
}
`;

let cs = `// <auto-generated by tools/gen-dialects.ts />
#nullable enable
using System.Collections.ObjectModel;
using InstrumentComponents.Kind;

namespace InstrumentComponents.Dialects;

public sealed class DialectProfile
{
    public required string Id { get; init; }
    public required InstrumentKind Kind { get; init; }
    public required string ManufacturerGlob { get; init; }
    public required string ModelGlob { get; init; }
    public required uint Channels { get; init; }
    public required IReadOnlyDictionary<string, string> Commands { get; init; }

    public string? Command(string key) => Commands.TryGetValue(key, out var v) ? v : null;

    public string? FormatCommand(string key, params (string Name, string Value)[] vars)
    {
        if (Command(key) is not { } tmpl) return null;
        foreach (var (name, value) in vars)
            tmpl = tmpl.Replace("{" + name + "}", value, StringComparison.Ordinal);
        return tmpl;
    }
}

public static class DialectRegistry
{
    public static readonly IReadOnlyList<DialectProfile> Profiles = new ReadOnlyCollection<DialectProfile>(new DialectProfile[]
    {
`;

for (const p of profiles) {
  const cmds = Object.entries(p.commands)
    .map(([k, v]) => `            ["${esc(k)}"] = "${esc(v)}"`)
    .join(",\n");
  cs += `        new DialectProfile
        {
            Id = "${esc(p.id)}",
            Kind = ${kindCs(p.kind)},
            ManufacturerGlob = "${esc(p.manufacturer_glob)}",
            ModelGlob = "${esc(p.model_glob)}",
            Channels = ${p.channels},
            Commands = new Dictionary<string, string>
            {
${cmds}
            },
        },
`;
}

cs += `    });

    static bool GlobMatch(string pat, string value)
    {
        value = value.ToLowerInvariant();
        pat = pat.ToLowerInvariant();
        if (pat == "*") return true;
        var starts = pat.StartsWith('*');
        var ends = pat.EndsWith('*');
        return (starts, ends) switch
        {
            (true, true) => pat.Length <= 2 || value.Contains(pat[1..^1], StringComparison.Ordinal),
            (false, true) => value.StartsWith(pat[..^1], StringComparison.Ordinal),
            (true, false) => value.EndsWith(pat[1..], StringComparison.Ordinal),
            _ => value == pat,
        };
    }

    public static DialectProfile Resolve(InstrumentKind kind, string? manufacturer = null, string? model = null)
    {
        var mfr = manufacturer ?? "";
        var mod = model ?? "";
        foreach (var profile in Profiles)
        {
            if (profile.Kind != kind) continue;
            if (GlobMatch(profile.ManufacturerGlob, mfr) && GlobMatch(profile.ModelGlob, mod))
                return profile;
        }
        return Profiles.First(p => p.Kind == kind && p.Id.StartsWith("generic_", StringComparison.Ordinal))
            ?? Profiles.First(p => p.Kind == kind);
    }
}
`;

const rustOut = new URL("crates/instrument-core/src/dialect.rs", root);
const csOut = new URL(
  "dotnet/src/InstrumentComponents/Dialects/DialectRegistry.cs",
  root,
);
await mkdir(new URL("dotnet/src/InstrumentComponents/Dialects/", root), {
  recursive: true,
});
await writeFile(rustOut, rust);
await writeFile(csOut, cs);
await rustfmtGenerated([rustOut]);
console.log("Wrote", rustOut.pathname);
console.log("Wrote", csOut.pathname);
console.log(`Profiles: ${profiles.length}`);
