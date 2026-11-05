import { execFile } from "node:child_process";
import { promisify } from "node:util";
import { fileURLToPath } from "node:url";

const execFileAsync = promisify(execFile);

/** Run rustfmt on generated Rust files so codegen matches `cargo fmt --check`. */
export async function rustfmtGenerated(urls: URL[]): Promise<void> {
  try {
    await execFileAsync("rustfmt", urls.map((url) => fileURLToPath(url)));
  } catch (error) {
    console.warn(
      "rustfmt failed or is not available; run `cargo fmt --all` after codegen.",
      error instanceof Error ? error.message : error,
    );
  }
}
