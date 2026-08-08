[← Docs index](README.md)

# CI, releases & upstream updates

- **`.github/workflows/ci.yml`** runs on every push to `main`, on all PRs, and on a daily schedule so
  SSP/SDK drift shows up on days with no commits. Four jobs, two of which gate a merge:

  | job | gates a merge? | why |
  |---|---|---|
  | `build-and-test` | **yes** | no external dependency; a failure is always our bug |
  | `store-test` | **yes** | real Postgres in a service container, no third-party network |
  | `integration-test` | no — `continue-on-error` | depends on Lightspark's hosted regtest; an outage there would block merges |
  | `funded-regtest-test` | no — `continue-on-error` | the above, plus it depends on a faucet-funded balance, so it goes red when the CI wallet drains — an operations event, not a defect in whichever PR was open |

  Both advisory jobs report their real pass/fail in the run summary. **Someone has to look**: an SDK
  error-string re-wording, or a preimage reaching the log, shows as a failed job inside a green run.
- **`.github/workflows/spark-regtest-wallet.yml`** is manual-only. It prints the CI regtest wallet's
  static deposit address and balance so a maintainer can fund it — see
  ["A funded regtest wallet for CI"](testing.md#a-funded-regtest-wallet-for-ci). It never prints the seed, and
  the seed generator refuses to run on a runner at all.
- **`.github/workflows/package.yml`** builds a `.btcpay` release artifact via BTCPay's
  `PluginPacker` on `v*` tags and on manual dispatch, uploads it (plus its `.btcpay.json` manifest
  and `SHA256SUMS`) as a workflow artifact, and attaches it to the corresponding GitHub Release when
  triggered by a tag. On a tag it also refuses to build if the tag disagrees with the version in the
  csproj, so a `v0.2.0` tag on a 0.1.0 tree fails rather than producing a mislabelled release.
  - **Artifacts are signed with keyless Sigstore build provenance**
    (`actions/attest-build-provenance`), not a maintainer GPG key: there is no long-lived key for
    anyone to generate, store, lose or leak, and the attestation binds the artifact's digest to this
    repository, this workflow and the commit that produced it rather than merely asserting who built
    it. Verify a download with
    `gh attestation verify BTCPayServer.Plugins.Flint.btcpay --repo sethforprivacy/flint`,
    or offline against the `attestation.jsonl` bundle attached to the release. The reasoning, and
    what this deliberately does *not* protect against, is in the header comment of
    [`package.yml`](../.github/workflows/package.yml).
- **Breez.Sdk.Spark / test / GitHub Actions version bumps** are handled by
  [Dependabot](../.github/dependabot.yml) (`nuget` and `github-actions` ecosystems); CI on the PRs
  it opens is the gate.
- **`btcpayserver` submodule bumps** are *not* handled by Dependabot: its `gitsubmodule` ecosystem
  tracks the latest commit on a branch, not release tags, which is the wrong model for a submodule
  pinned to stable releases. Instead, **`.github/workflows/btcpayserver-update.yml`** runs weekly
  (and on manual dispatch) to look for the newest stable `vX.Y.Z` btcpayserver tag past the one
  currently pinned, and if it finds one, opens a PR that bumps the submodule and updates
  `Constants.BuiltAgainstBTCPayServerVersion` (and the mention of the pin in [Building](building.md)) to match, leaving the
  declared support floor alone — see the
  discovery logic in `scripts/check-btcpayserver-update.sh`.
- **Branch protection**: not configured by these workflows. For `main`, enable "Require status
  checks to pass before merging" with `ci.yml`'s `build-and-test` **and `store-test`** jobs required
  (and `integration-test` and `funded-regtest-test` intentionally *not* required, since both are
  `continue-on-error` by design), plus "Require branches to be up to date before merging".
