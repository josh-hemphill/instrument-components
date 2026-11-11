# Batch trusted-publisher setup

This helper provisions policy configuration only. It does not publish packages,
create tags, change owners, replace policies, or delete policies. The reviewed
package/workflow mapping is [../../spec/publishers.json](../../spec/publishers.json).
The default command prints the plan without contacting a registry:

```sh
python tools/publisher-setup/provision.py
```

The manifest contains 12 exact crate names and seven NuGet policies covering 13
exact package IDs. There are no wildcard package scopes. The five external
repositories use `driver-preview.yml`; SDK and root packages have separate
workflows. Keep this manifest and those filenames synchronized.

## NuGet: one sign-in for the whole batch

Run on a machine with a visible desktop and Python 3.11 or newer. Use the NuGet
account that owns the existing InstrumentComponents packages; the manifest
currently names `josh-hemphill`. No NuGet API key is needed.
The NuGet owner name may differ from the GitHub owner name. If it does, update
`nugetAccount` and each NuGet policy's `owner` in the manifest to the confirmed
NuGet owner; keep the GitHub repository owner fields as they are. The helper
reports `OWNER_NOT_AVAILABLE` with the available NuGet owner names, or
`OWNER_PERMISSIONS_REVIEW_REQUIRED` with the missing push permissions, and stops
without creating policies. These statuses return exit code 2.

```sh
python -m pip install playwright
python -m playwright install chromium
python tools/publisher-setup/provision.py --service nuget
```

Sign in through the browser's normal NuGet/Microsoft login. The first run reports
what would be created. To create the seven missing policies in another signed-in
batch, run:

```sh
python tools/publisher-setup/provision.py --service nuget --apply --report nuget-policy-report.json
```

The script uses an ephemeral browser profile, the account page's own CSRF helper,
and same-origin requests. The profile is deleted when the browser closes. It does
not export cookies or session credentials. You can select an existing Chromium
installation with `--browser-executable /path/to/chromium`.

On Linux, the helper also puts Chrome's configuration, cache, launcher data, and
crash-report paths inside the temporary directory. This allows graphical login
when the regular home configuration directories are read-only in a sandbox.
It waits for Microsoft sign-in to return to NuGet before checking the policy
editor, so the login page's content security policy is respected.

If the graphical browser's GPU process crashes with the Linux graphics driver,
use software rendering and X11 (requires a working X11/XWayland display):

```sh
python tools/publisher-setup/provision.py --service nuget --browser-executable /usr/bin/google-chrome --browser-arg=--disable-gpu --browser-arg=--ozone-platform=x11
```

These flags affect only this browser launch. Policy lookup failures include a
fixed diagnostic reason in the progress report without exporting page contents
or credentials.

### Hardware security keys on Linux

A browser launched inside a task sandbox may not see `/dev/hidraw*`, even when
the host detects a USB YubiKey. FIDO/WebAuthn needs access to the key's HID device;
a writable temporary browser profile does not supply that access. In this
environment, the YubiKey FIDO device was readable and writable by the desktop
user on the host but absent inside the task sandbox. This is a concrete blocker
to hardware-key sign-in; an actual passkey sign-in outside the task sandbox is
still needed to verify the complete flow.

Use regular Chrome launched from the desktop for crates.io/GitHub sign-in with
the YubiKey. For the crates.io helper, that browser is needed only to obtain the
short-lived configuration token described below; the batch itself uses the
token and does not automate GitHub sign-in. Keep the token out of chat, source
files, and shell history. Do not copy the regular browser profile or export its
cookies, change system-wide device permissions, or disable FIDO authentication
to work around a task sandbox that hides hardware devices.

References: [Yubico FIDO HID access rules](https://github.com/Yubico/libfido2/blob/main/udev/70-u2f.rules)
and [GitHub passkey sign-in](https://docs.github.com/en/authentication/authenticating-with-a-passkey/signing-in-with-a-passkey).

Existing matching policies are preserved. The account page currently supplies
localized scope descriptions, so the helper may report
`EXISTING_SCOPE_REVIEW_REQUIRED` instead of guessing that an existing policy is
correct. Check that its owner, repository numeric IDs, workflow, enabled state,
exact package subjects, and push scope match the plan. Broad existing policies
are never silently accepted or narrowed. A newly created policy is checked
against the requested identity and package set, then read back after reloading
the account page with the same policy ID. Confirm activation on the account
page; creating a policy alone is not evidence of a successful OIDC publication.

## crates.io: temporary policy-configuration token

Crates.io currently requires the crate to exist before its policy can be created.
The three existing root crates can be configured now. The four SDK crates and
five external driver crates need their separately approved first publication
before their policies can be provisioned. `FIRST_PUBLICATION_REQUIRED` reports
this prerequisite; this helper never attempts that publication.

Create a short-lived token at <https://crates.io/settings/tokens> with the trusted
publishing configuration permission, restricted to the applicable exact crate
names. The authenticated user must be an individual crate owner with a verified
email and a linked GitHub account. Set the token as the process environment
variable `CRATES_IO_TRUSTED_PUBLISHING_TOKEN` through your terminal's secret-input
facility or an environment secret. Do not paste it into chat, source files, or
shell command history.

Alternatively, run with `--prompt-token` in an interactive terminal. The helper
reads the token with a hidden prompt and keeps it only in process memory:

```sh
python tools/publisher-setup/provision.py --service crates --apply --prompt-token --report crates-policy-report.json
```

For the initial three existing crates, select only `trusted-publishing`
("Manage trusted publishing configurations") and restrict the token to the exact
names `instrument-core`, `instrument-visa`, and `instrument-components`. Use a
short expiration and revoke it after this batch. No publish permission is needed.

```sh
python tools/publisher-setup/provision.py --service crates
python tools/publisher-setup/provision.py --service crates --apply --report crates-policy-report.json
```

Revoke the temporary token after the batch. After the initial publications,
rerun the batch with a configuration token scoped to the newly created names.
Routine subsequent releases use GitHub OIDC, without a standing publish token.
A newly created crate policy is read back and matched by policy ID, exact
configuration, and numeric GitHub owner ID before it is reported as created.
A publish-only token cannot configure trusted-publisher policies. The helper
returns exit code 2 when first publication, authentication, or scope review is
still required; otherwise failures return a nonzero code and stop the batch.
A post-write mismatch reports `CREATION_VERIFICATION_REQUIRED`; an interrupted request reports `CREATION_OUTCOME_UNKNOWN`. Inspect the account and saved progress before retrying. The report contains policy identity/status, never credentials. A write is not rolled back automatically. It is safe to rerun: matching crate policies are skipped and other policies
remain untouched.

## Verification and source assumptions

```sh
python -m unittest discover -s tools/publisher-setup -p 'test_*.py' -v
```

The offline suite needs Node.js for its isolated NuGet account-page fixture. It
checks exact scope, dry runs, repeated runs, existing/conflicting/disabled
policies, origin restrictions, pagination, errors and returned policy identity.
It performs no authentication, publication or registry writes.

This automation uses the registries' current account-management interfaces,
verified against the pinned source links in the manifest. NuGet's account-page
interface is not a promised public management API; unexpected page shapes stop
without writing. Retest after registry UI changes. Final release validation must
still exercise each actual workflow's OIDC login and publication against public
registries after release approval. Artifact-bootstrap package tests do not prove
that registry publisher permissions are configured.

## First-publication bootstrap (after preview release approval)

The SDK and five vendor crate workflows accept an optional temporary repository
secret `CARGO_BOOTSTRAP_TOKEN`. It is used only instead of OIDC for the crates
publish job. With that secret absent, the workflows use their trusted publisher.
NuGet continues to use OIDC with the policies created above.

After the reviewed previews are approved, create one short-lived crates.io token
with first-publication permission for the nine new SDK/driver names. Apply the
narrowest name restrictions offered by the registry.
Provide it through `CARGO_BOOTSTRAP_TOKEN` in the environment. The GitHub CLI
must have permission to manage Actions secrets in the six exact repositories.
One batch configures the initial secret, preserving any existing secret:

```sh
python tools/publisher-setup/bootstrap-secrets.py
python tools/publisher-setup/bootstrap-secrets.py --apply
```

To enter the first-publication token without setting an environment variable,
use the hidden prompt in an interactive terminal:

```sh
python tools/publisher-setup/bootstrap-secrets.py --apply --prompt-token
```

This prompt sends the token only on the GitHub CLI's standard input and does
not export it into the environment or print it in reports.

This creates no release tags and publishes nothing. Release the SDK preview
first, then the facade/integration packages, then the five driver previews.
After each new crate exists, the policy helper can configure it. Once all12
crate policies are verified, remove the temporary secrets with one batch and
revoke the token:

```sh
python tools/publisher-setup/bootstrap-secrets.py --remove --apply
```

An existing secret reports `EXISTING_SECRET_PRESERVED`; account-owner review is
needed before replacing it. Keep the batch output if a request fails midway.
The configuration-only token described earlier does not have publication rights;
use only the narrow permissions appropriate to each separate step. Authentication,
release approval and actual OIDC/public-registry execution are still required;
none has been performed by preparing these scripts.
