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

Existing matching policies are preserved. The account page currently supplies
localized scope descriptions, so the helper may report
`EXISTING_SCOPE_REVIEW_REQUIRED` instead of guessing that an existing policy is
correct. Check that its owner, repository numeric IDs, workflow, enabled state,
exact package subjects, and push scope match the plan. Broad existing policies
are never silently accepted or narrowed. A newly created policy is checked
against the requested identity and package set. Confirm activation on the account
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

```sh
python tools/publisher-setup/provision.py --service crates
python tools/publisher-setup/provision.py --service crates --apply --report crates-policy-report.json
```

Revoke the temporary token after the batch. After the initial publications,
rerun the batch with a configuration token scoped to the newly created names.
Routine subsequent releases use GitHub OIDC, without a standing publish token.
A publish-only token cannot configure trusted-publisher policies. The helper
returns exit code 2 when first publication, authentication, or scope review is
still required; otherwise failures return a nonzero code and stop the batch.
A post-write mismatch reports `CREATION_VERIFICATION_REQUIRED`; an interrupted request reports `CREATION_OUTCOME_UNKNOWN`. Inspect the account and saved progress before retrying. The report contains policy identity/status, never credentials. A write is not rolled back automatically. It is safe to rerun: matching crate policies are skipped and other policies
remain untouched.

## Verification and source assumptions

```sh
python -m unittest discover -s tools/publisher-setup -p 'test_*.py' -v
```

The eleven-test offline suite needs Node.js for its isolated NuGet account-page fixture. It
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
