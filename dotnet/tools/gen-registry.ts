import { readFile, writeFile } from "node:fs/promises";
/**
 * Converts model_registry.toml to embedded JSON for the C# package.
 */
const tomlPath = new URL(
  "../../crates/instrument-core/data/model_registry.toml",
  import.meta.url,
);
const outPath = new URL(
  "../src/InstrumentComponents/Data/model_registry.json",
  import.meta.url,
);

const text = await readFile(tomlPath, "utf8");
const entries: Array<{ manufacturer: string; model: string; kinds: string[] }> =
  [];
const usbEntries: Array<{
  vid: string;
  pid: string;
  manufacturer?: string;
  model?: string;
  kinds: string[];
}> = [];

let current: Record<string, unknown> | null = null;
let section: "entry" | "usb_entry" | null = null;

for (const line of text.split("\n")) {
  const trimmed = line.trim();
  if (!trimmed || trimmed.startsWith("#")) continue;
  if (trimmed === "[[entry]]") {
    if (current && section === "entry") entries.push(current as typeof entries[0]);
    current = {};
    section = "entry";
    continue;
  }
  if (trimmed === "[[usb_entry]]") {
    if (current && section === "entry") entries.push(current as typeof entries[0]);
    if (current && section === "usb_entry") usbEntries.push(current as typeof usbEntries[0]);
    current = {};
    section = "usb_entry";
    continue;
  }
  const eq = trimmed.indexOf("=");
  if (eq < 0 || !current) continue;
  const key = trimmed.slice(0, eq).trim();
  let value = trimmed.slice(eq + 1).trim();
  if (value.startsWith('"') && value.endsWith('"')) {
    current[key] = value.slice(1, -1);
  } else if (value.startsWith("[") && value.endsWith("]")) {
    current[key] = value
      .slice(1, -1)
      .split(",")
      .map((s) => s.trim().replace(/^"|"$/g, ""));
  }
}

if (current && section === "entry") entries.push(current as typeof entries[0]);
if (current && section === "usb_entry") usbEntries.push(current as typeof usbEntries[0]);

const output = JSON.stringify({ entries, usbEntries }, null, 2) + "\n";
await writeFile(outPath, output);
console.log(`Wrote ${outPath.pathname}`);
