#!/usr/bin/env bash
set -euo pipefail

# Configures the BTCPay Server developer environment to load this plugin during a debug session.
# Adapted from the official btcpayserver-plugin-template for this repository's flat layout
# (plugin project and btcpayserver submodule are both directly under the repo root).

cd "$(dirname "$0")"

source plugin-env.sh

TARGET_PATH="$(dotnet build "$PROJECT/$PROJECT.csproj" -p:Configuration=Debug -getProperty:TargetPath)"

printf '{ "DEBUG_PLUGINS": "%s" }' "$TARGET_PATH" > "btcpayserver/BTCPayServer/appsettings.dev.json"

echo "The plugin will now start when debugging BTCPay Server"
