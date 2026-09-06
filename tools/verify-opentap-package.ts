/**
 * Verify the OpenTAP .TapPackage contains the plugin and core DLLs, not VISA.
 */
const root = new URL("..", import.meta.url);
const searchDir = new URL("dotnet/src/InstrumentComponents.OpenTap/bin/Release/net8.0/", root);

function findPackage(): URL {
  if (!Deno.statSync(searchDir).isDirectory) {
    throw new Error(`missing ${searchDir.pathname}; build with -p:CreateOpenTapPackage=true`);
  }
  const matches = [...Deno.readDirSync(searchDir)]
    .filter((entry) => entry.isFile && entry.name.endsWith(".TapPackage"))
    .map((entry) => new URL(entry.name, searchDir));
  if (matches.length !== 1) {
    throw new Error(`expected one .TapPackage in ${searchDir.pathname}, found ${matches.length}`);
  }
  return matches[0];
}

async function unzipList(archive: URL): Promise<string[]> {
  const command = new Deno.Command("unzip", { args: ["-Z", "-1", archive.pathname], stdout: "piped", stderr: "piped" });
  const result = await command.output();
  if (result.code !== 0) {
    throw new Error(new TextDecoder().decode(result.stderr));
  }
  return new TextDecoder().decode(result.stdout).split("\n").map((line) => line.trim()).filter(Boolean);
}

async function unzipFile(archive: URL, inner: string): Promise<string> {
  const command = new Deno.Command("unzip", {
    args: ["-p", archive.pathname, inner],
    stdout: "piped",
    stderr: "piped",
  });
  const result = await command.output();
  if (result.code !== 0) {
    throw new Error(new TextDecoder().decode(result.stderr));
  }
  return new TextDecoder().decode(result.stdout);
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
