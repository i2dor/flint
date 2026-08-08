#!/usr/bin/env bash
set -euo pipefail

# Discovers whether a newer *stable* btcpayserver release tag exists than the
# one currently pinned (submodule + Constants.BuiltAgainstBTCPayServerVersion). Prints
# a single-line JSON object and exits 0 whether or not an update was found;
# it is the caller's job to decide what to do with the result.
#
# Deliberately does NOT touch the submodule checkout or the working tree: it
# only needs network access to the upstream remote's tag list, so it stays
# cheap enough to run unconditionally on a weekly schedule. The apply step
# (submodule bump + Constants.cs edit) lives in the calling workflow.
#
# Usage: scripts/check-btcpayserver-update.sh
# Output: {"current":"2.4.1","latest":"2.4.1","changed":false,"release_url":"..."}

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
CONSTANTS_FILE="$REPO_ROOT/BTCPayServer.Plugins.Flint/Constants.cs"
SUBMODULE_URL="https://github.com/btcpayserver/btcpayserver.git"

if [ ! -f "$CONSTANTS_FILE" ]; then
  echo "error: $CONSTANTS_FILE not found" >&2
  exit 1
fi

# BuiltAgainstBTCPayServerVersion, not MinBTCPayServerVersion. The two are deliberately
# different: the plugin builds against the newest pinned tag but declares support for an
# older floor, and that floor is a support decision this automation must not touch. Anchored
# so the grep cannot match the "Min" constant on the line above it.
current="$(grep -oE 'BuiltAgainstBTCPayServerVersion = "[0-9]+\.[0-9]+\.[0-9]+"' "$CONSTANTS_FILE" \
  | grep -oE '[0-9]+\.[0-9]+\.[0-9]+' || true)"

if [ -z "$current" ]; then
  echo "error: could not find BuiltAgainstBTCPayServerVersion in $CONSTANTS_FILE" >&2
  exit 1
fi

# Stable release tags only: vX.Y.Z with no suffix. This deliberately excludes
# -rc*, -rockstar-rc*, and one-off tags that aren't version numbers at all
# (btcpayserver's tag list includes e.g. "vshopify", "vu2ftest", "zlndseedbackup").
tags=()
while IFS= read -r line; do
  [ -n "$line" ] && tags+=("$line")
done < <(
  git ls-remote --tags --refs "$SUBMODULE_URL" \
    | awk '{print $2}' \
    | sed -n 's#^refs/tags/v\([0-9][0-9]*\.[0-9][0-9]*\.[0-9][0-9]*\)$#\1#p'
)

if [ "${#tags[@]}" -eq 0 ]; then
  echo "error: no stable vX.Y.Z tags found on $SUBMODULE_URL" >&2
  exit 1
fi

# Plain numeric field sort (portable across GNU and BSD sort — no reliance on
# GNU-only `sort -V`).
latest="$(printf '%s\n' "${tags[@]}" | sort -t. -k1,1n -k2,2n -k3,3n | tail -n1)"

# "changed" means latest is strictly newer than current: not equal, and the
# max of the two (by the same numeric sort) is latest.
changed="false"
if [ "$latest" != "$current" ]; then
  max="$(printf '%s\n%s\n' "$current" "$latest" | sort -t. -k1,1n -k2,2n -k3,3n | tail -n1)"
  if [ "$max" = "$latest" ]; then
    changed="true"
  fi
fi

printf '{"current":"%s","latest":"%s","changed":%s,"release_url":"https://github.com/btcpayserver/btcpayserver/releases/tag/v%s"}\n' \
  "$current" "$latest" "$changed" "$latest"
