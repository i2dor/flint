#!/usr/bin/env bash
set -euo pipefail

# Configures the BTCPay Server developer environment to load this plugin during a debug session.
# Adapted from the official btcpayserver-plugin-template for this repository's flat layout
# (plugin project and btcpayserver submodule are both directly under the repo root).

cd "$(dirname "$0")"

source plugin-env.sh

TARGET_PATH="$(dotnet build "$PROJECT/$PROJECT.csproj" -p:Configuration=Debug -getProperty:TargetPath)"

# jq rather than a printf: the target path is arbitrary filesystem text, and a path
# containing a quote or a backslash (any Windows path) would otherwise be interpolated
# into the file as invalid JSON — which BTCPay's dev host then fails to parse, with an
# error that does not name this file.
jq -n --arg p "$TARGET_PATH" '{DEBUG_PLUGINS:$p}' > "btcpayserver/BTCPayServer/appsettings.dev.json"

echo "The plugin will now start when debugging BTCPay Server"
