#!/usr/bin/env python3
"""Re-generate the obfuscated Breez API key literal in Constants.cs.

This is NOT encryption and is not pretending to be. The mask is right here in the
repository, so anyone who wants the key has it in under a minute -- and that is fine,
because Breez API keys are per-application identifiers, not per-user credentials, and
this one is embedded in a publicly distributed assembly by design.

What it does buy: the key is no longer a plain string in a public repository, so it is
not harvested by GitHub code search, automated secret scanners, or `strings` over the
shipped assembly. It stops casual copy-paste reuse, nothing more.

The key cannot simply be injected at build time the way an app with its own release pipeline would, because the
BTCPay plugin registry builds plugins from source: it clones this repository and runs
`dotnet publish`, so a CI-only secret would leave every registry-installed copy with no
key at all.

Usage:  python3 scripts/obfuscate-api-key.py '<raw breez api key>'
Then paste the printed literal into Constants.ObfuscatedBreezApiKey.
"""
import base64
import itertools
import sys

MASK = b"BTCPayServer.Plugins.Flint"


def obfuscate(raw: str) -> str:
    blob = bytes(b ^ m for b, m in zip(raw.encode(), itertools.cycle(MASK)))
    return base64.b64encode(blob).decode()


def deobfuscate(encoded: str) -> str:
    blob = base64.b64decode(encoded)
    return bytes(b ^ m for b, m in zip(blob, itertools.cycle(MASK))).decode()


if __name__ == "__main__":
    if len(sys.argv) != 2:
        sys.exit(__doc__)
    out = obfuscate(sys.argv[1])
    assert deobfuscate(out) == sys.argv[1], "round-trip failed"
    print(out)
