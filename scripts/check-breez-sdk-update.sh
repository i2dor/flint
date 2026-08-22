#!/usr/bin/env bash
set -euo pipefail

# Discovers whether a newer Breez.Sdk.Spark version exists that upstream has published a
# *GitHub Release* for — not merely tagged and pushed to NuGet. Prints a single-line JSON
# object and exits 0 whether or not an update was found; it is the caller's job to decide
# what to do with the result.
#
# Why release-gated rather than Dependabot: Breez tags and publishes patch versions with no
# release entry and no notes (0.22.1 through 0.22.3 all shipped that way), and a PR per
# NuGet push is churn this repo does not want. A version upstream stands behind with a
# published release is the signal worth a PR. The trade-off is deliberate: a fix Breez ships
# tag-only waits until they cut a release, or until a human bumps the pin by hand — which
# remains possible and is how the tag-only 0.22.2 and 0.22.3 bumps happened.
#
# Requires: gh (authenticated — in Actions, GH_TOKEN from the default token), curl, jq.
#
# Usage: scripts/check-breez-sdk-update.sh
# Output: {"current":"0.22.3","latest":"0.22.3","changed":false,"release_url":"..."}

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
CSPROJ="$REPO_ROOT/BTCPayServer.Plugins.Flint/BTCPayServer.Plugins.Flint.csproj"
UPSTREAM="breez/spark-sdk"
NUGET_INDEX="https://api.nuget.org/v3-flatcontainer/breez.sdk.spark/index.json"

if [ ! -f "$CSPROJ" ]; then
  echo "error: $CSPROJ not found" >&2
  exit 1
fi

current="$(grep -oE '<PackageReference Include="Breez.Sdk.Spark" Version="[0-9]+\.[0-9]+\.[0-9]+(\.[0-9]+)?"' "$CSPROJ" \
  | grep -oE '[0-9]+\.[0-9]+\.[0-9]+(\.[0-9]+)?' || true)"

if [ -z "$current" ]; then
  echo "error: could not find the Breez.Sdk.Spark PackageReference in $CSPROJ" >&2
  exit 1
fi

# Published, non-draft, non-prerelease GitHub Releases whose tag is a plain stable version.
# Breez's tags carry no v prefix, and their explicit prereleases ("0.20.0-dev1") are excluded
# by the pattern as well as by the prerelease flag.
releases=()
while IFS= read -r line; do
  [ -n "$line" ] && releases+=("$line")
done < <(
  gh api "repos/$UPSTREAM/releases?per_page=100" \
    --jq '.[] | select(.draft == false and .prerelease == false) | .tag_name' \
    | sed -n 's#^v\{0,1\}\([0-9][0-9]*\.[0-9][0-9]*\.[0-9][0-9]*\)$#\1#p'
)

if [ "${#releases[@]}" -eq 0 ]; then
  echo "error: no stable releases found on $UPSTREAM" >&2
  exit 1
fi

# Plain numeric field sort, as in check-btcpayserver-update.sh — no reliance on GNU `sort -V`.
latest="$(printf '%s\n' "${releases[@]}" | sort -t. -k1,1n -k2,2n -k3,3n | tail -n1)"

# The release must also be installable: a GitHub Release whose NuGet package has not landed
# yet (or never will — releases cover more platforms than the .NET binding) must not produce
# a PR whose restore fails. Reported as unchanged, and picked up once the package appears.
if ! curl -fsSL "$NUGET_INDEX" | jq -e --arg v "$latest" '.versions | index($v)' > /dev/null; then
  echo "note: $UPSTREAM release $latest has no matching NuGet package yet; treating as no update" >&2
  latest="$current"
fi

# "changed" means latest is strictly newer than current: not equal, and the max of the two
# (by the same numeric sort) is latest. A current pin *ahead* of the newest release — a
# hand-applied tag-only bump, exactly how 0.22.2 and 0.22.3 arrived — therefore stays put.
changed="false"
if [ "$latest" != "$current" ]; then
  max="$(printf '%s\n%s\n' "$current" "$latest" | sort -t. -k1,1n -k2,2n -k3,3n | tail -n1)"
  if [ "$max" = "$latest" ]; then
    changed="true"
  fi
fi

printf '{"current":"%s","latest":"%s","changed":%s,"release_url":"https://github.com/%s/releases/tag/%s"}\n' \
  "$current" "$latest" "$changed" "$UPSTREAM" "$latest"
