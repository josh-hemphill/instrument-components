import { execFile } from "node:child_process";
import { promisify } from "node:util";
import { fileURLToPath } from "node:url";
import { readFileSync, readdirSync, statSync } from "node:fs";
/**
 * Verify the OpenTAP .TapPackage contains the plugin and core DLLs, not VISA.
 */
const root = new URL("..", import.meta.url);
const searchDir = new URL("dotnet/src/InstrumentComponents.OpenTap/bin/Release/net8.0/", root);

function findPackage(): URL {
  if (!statSync(searchDir).isDirectory()) {
    throw new Error(`missing ${searchDir.pathname}; build with -p:CreateOpenTapPackage=true`);
  }
  const matches = [...readdirSync(searchDir, { withFileTypes: true })]
    .filter((entry) => entry.isFile() && entry.name.endsWith(".TapPackage"))
    .map((entry) => new URL(entry.name, searchDir));
  if (matches.length !== 1) {
    throw new Error(`expected one .TapPackage in ${searchDir.pathname}, found ${matches.length}`);
  }
  return matches[0];
}

const execFileAsync = promisify(execFile);

async function unzipList(archive: URL): Promise<string[]> {
  const { stdout } = await execFileAsync("unzip", ["-Z", "-1", fileURLToPath(archive)], { encoding: "utf8" });
  return stdout.split("\n").map((line) => line.trim()).filter(Boolean);
}

async function unzipFile(archive: URL, inner: string): Promise<string> {
  const { stdout } = await execFileAsync("unzip", ["-p", fileURLToPath(archive), inner], { encoding: "utf8" });
  return stdout;
}

async function main() {
  const archive = findPackage();
  const files = await unzipList(archive);
  const required = [
    "InstrumentComponents.OpenTap.dll",
    "InstrumentComponents.dll",
    "Packages/InstrumentComponents.OpenTap/package.xml",
  ];
  for (const name of required) {
    if (!files.includes(name)) {
      throw new Error(`${archive.pathname} missing ${name}. files: ${files.join(", ")}`);
    }
  }
  const forbidden = files.filter((name) =>
    name.toLowerCase().includes("visa") || name.toLowerCase().includes("ivi")
  );
  if (forbidden.length > 0) {
    throw new Error(`${archive.pathname} must not contain VISA/IVI files: ${forbidden.join(", ")}`);
  }

  const xml = await unzipFile(archive, "Packages/InstrumentComponents.OpenTap/package.xml");
  if (!xml.includes('Name="InstrumentComponents.OpenTap"')) {
    throw new Error("package.xml is missing Name=InstrumentComponents.OpenTap");
  }
  const expectedVersion = process.argv[2] ?? readFileSync(new URL("dotnet/src/InstrumentComponents.OpenTap/package.xml", root), "utf8").match(/<Package\b[^>]*\bVersion="([^"]+)"/)?.[1];
  const actualVersion = xml.match(/<Package\b[^>]*\bVersion="([^"]+)"/)?.[1];
  if (!expectedVersion || actualVersion !== expectedVersion) {
    throw new Error(`TapPackage version ${actualVersion} disagrees with ${expectedVersion}`);
  }
  if (!archive.pathname.endsWith(`.${expectedVersion}.TapPackage`)) {
    throw new Error(`TapPackage filename disagrees with ${expectedVersion}`);
  }
  if (!xml.includes("OpenTAP")) {
    throw new Error("package.xml is missing the OpenTAP dependency");
  }
  if (/InstrumentComponents\.Visa|Ivi\.Visa|GlobalResourceManager/.test(xml)) {
    throw new Error("package.xml must not reference VISA");
  }
  console.log(`ok ${archive.pathname}`);
  for (const name of files) {
    console.log(`  ${name}`);
  }
}

await main();
