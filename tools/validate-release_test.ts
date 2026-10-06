import { validateRelease } from "./validate-release.ts";

Deno.test("matching current versions pass", () => {
  const version = Deno.readTextFileSync(new URL("../Cargo.toml", import.meta.url)).match(/\[workspace.package\][\s\S]*?version\s*=\s*"([^"]+)"/)![1];
  if (validateRelease(`v${version}`) !== version) throw new Error("wrong version");
});
Deno.test("mismatched and malformed release tags fail", () => {
  for (const tag of ["v99999.0.0", "0.1.0", "v01.1.0", "v0.1", "v0.1.0;echo bad"]) {
    let rejected = false;
    try { validateRelease(tag); } catch { rejected = true; }
    if (!rejected) throw new Error(`accepted ${tag}`);
  }
});
