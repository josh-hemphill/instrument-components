// Release tags must agree with package versions and internal Rust dependency requirements.
const root = new URL("../", import.meta.url);
export function validateRelease(tag: string): string {
  if (!/^v(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-[0-9A-Za-z.-]+)?$/.test(tag)) throw new Error(`Invalid release tag: ${tag}`);
  const version = tag.slice(1);
  const prerelease = version.includes("-") ? version.slice(version.indexOf("-") + 1) : "";
  if (prerelease && prerelease.split(".").some(id => !id || (/^[0-9]+$/.test(id) && id.length > 1 && id.startsWith("0")))) throw new Error(`Invalid release tag: ${tag}`);
  const cargo = Deno.readTextFileSync(new URL("Cargo.toml", root));
  const actual = cargo.match(/\[workspace.package\][\s\S]*?version\s*=\s*"([^"]+)"/)?.[1];
  if (actual !== version) throw new Error(`Tag ${tag} disagrees with Cargo version ${actual}`);
  for (const name of ["instrument-core", "instrument-visa"]) {
    const dependency = cargo.match(new RegExp(`${name} = \{[^\n]*version = "([^"]+)"`))?.[1];
    if (dependency !== version) throw new Error(`${name} dependency ${dependency} disagrees with ${version}`);
  }
  for (const name of ["InstrumentComponents", "InstrumentComponents.Visa"]) {
    const project = Deno.readTextFileSync(new URL(`dotnet/src/${name}/${name}.csproj`, root));
    const packageVersion = project.match(/<Version>([^<]+)<\/Version>/)?.[1];
    if (packageVersion !== version) throw new Error(`${name} version ${packageVersion} disagrees with ${version}`);
  }
  return version;
}
if (import.meta.main) console.log(validateRelease(Deno.args[0] ?? ""));
