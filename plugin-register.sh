#!/usr/bin/env bash
set -euo pipefail

# Configures the BTCPay Server developer environment to load this plugin during a debug session.
# Adapted from the official btcpayserver-plugin-template for this repository's flat layout
# (plugin project and btcpayserver submodule are both directly under the repo root).

cd "$(dirname "$0")"

source plugin-env.sh

# Guarded before anything is written: the redirect below truncates its target
# before jq would fail, and a half-written appsettings.dev.json breaks BTCPay's
# dev host with an error that does not name this file.
command -v jq >/dev/null || { echo 'error: plugin-register.sh needs jq (brew install jq / apt install jq)' >&2; exit 1; }

TARGET_PATH="$(dotnet build "$PROJECT/$PROJECT.csproj" -p:Configuration=Debug -getProperty:TargetPath)"

# jq rather than a printf: the target path is arbitrary filesystem text, and a path
# containing a quote or a backslash (any Windows path) would otherwise be interpolated
# into the file as invalid JSON — which BTCPay's dev host then fails to parse, with an
# error that does not name this file.
jq -n --arg p "$TARGET_PATH" '{DEBUG_PLUGINS:$p}' > "btcpayserver/BTCPayServer/appsettings.dev.json"

echo "The plugin will now start when debugging BTCPay Server"
