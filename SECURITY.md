# Security Policy

This plugin is developed and maintained by
**[sethforprivacy](https://github.com/sethforprivacy)**. It runs inside a merchant's BTCPay Server,
holds the encrypted recovery phrase for each store's Lightning wallet, and moves money without
asking — so security reports are welcome and taken seriously.

> **This plugin is not affiliated with Spark, Breez, Lightspark or Flashnet.** It integrates the
> Breez Spark SDK; it is not published by any of them. A separate, unrelated plugin using a similar
> name exists on the BTCPay plugin registry. The only vulnerability disclosure channel for *this*
> plugin is the one listed on this page.

## Reporting a vulnerability

**Please do not open a public issue, pull request, or social-media post for a security
vulnerability.** Public disclosure before a fix is available puts merchants' funds at risk, and
BTCPay does not auto-update plugins — so the window between disclosure and the average operator
upgrading is long.

Use a **[GitHub private security advisory](https://github.com/sethforprivacy/flint/security/advisories/new)**.
It gives you a private, structured thread with the maintainer, keeps the report, the fix and the
eventual disclosure in one place, and is the only channel for this project — there is deliberately no
security mailbox to go stale or be missed.

If you cannot use GitHub, open a public issue containing **no details** — just that you have a
security report and how to reach you — and a private channel will be arranged from there.

**Never include a real recovery phrase, an unredacted server log, or a live API key in a report.**
Seed material ends up in issue trackers, search indexes and inboxes forever. If a report needs a
phrase to demonstrate the bug, generate a fresh one, fund it with a trivial amount, and say that is
what you did.

### What to include

- A clear description of the issue and its security impact.
- A working proof of concept is required. It must run against this plugin itself — a packaged
  release or a local build installed in a real BTCPay Server — and demonstrate the reported
  behaviour. A standalone script, a mathematical argument, or a simulation that reproduces the
  theory without exercising the plugin does not satisfy this.
- Step-by-step reproduction instructions.
- The plugin version (the number on its page in BTCPay, e.g. `0.3.0.0`), the BTCPay Server version,
  and the network (mainnet, testnet, regtest).
- Whether it involves a sweep, and if so the destination mode — on-chain wallet, fixed address, or
  cross-chain — since those paths differ substantially.
- Any relevant logs, addresses, or transaction ids, with seed material removed.
- If it only reproduces intermittently, say so rather than leaving it out. Several of the worst bugs
  in this plugin's history were races, and a report that hides the flakiness hides the bug.

If you used AI tooling to find or write up the report, please say so.

### Communication expectations

AI should not be used to generate comments when communicating with the maintainer and other
contributors. Comments are expected to be written by humans. Comments believed to be AI-written may
be moderated.

## Our commitment (safe harbour)

Security research conducted in good faith under this policy is considered authorised, and will not
be met with legal action, provided you:

- make a good-faith effort to avoid privacy violations, data destruction, and interruption or
  degradation of any service;
- only interact with BTCPay instances, stores and funds you own or have explicit written permission
  to access — testing against a merchant's live instance without that permission is not good-faith
  research and is not covered;
- avoid denial of service against shared infrastructure, in particular the SDK's service providers
  and the cross-chain route provider, which are third parties this plugin merely calls; and
- give a reasonable opportunity to fix an issue before disclosing it publicly.

If in doubt about whether an action is authorised, ask first via a private advisory.

## What to expect

- **Acknowledgement:** within **3 business days**. This is a small project maintained by one
  person; if you have not heard back, a polite nudge on the advisory thread is welcome.
- **Triage and initial assessment:** within **10 business days**.
- **Coordinated disclosure:** the aim is to ship a fix and coordinate public disclosure within
  **90 days** of the report, with progress updates and a disclosure date agreed with you. If a
  report is being actively exploited against real merchants, the fix ships first and the explanation
  follows.
- **Credit:** with your permission, you will be credited in the advisory and the changelog once a
  fix is released.

## Scope

**In scope** — the code in this repository and the artifacts it publishes, in particular anything
that could lead to:

- **Exposure of seed material.** The store's recovery phrase is encrypted at rest with ASP.NET Data
  Protection and must never reach a log, an API response, a view, or a crash report. A path that
  leaks it, or that weakens the encryption, is the most serious class of bug this project can have.
- **Money moving to the wrong place, twice, or not at all** — sweep destination selection,
  cross-chain destination resolution, idempotency of sends, and the classification of a send whose
  outcome is unknown.
- **Cross-store isolation failures** — one store reading, configuring, sweeping or spending
  another store's wallet, through the UI or the Greenfield API. (The Lightning connection string is
  store-bound at save time and cross-store configurations are swept at startup with the victim's key
  rotated; the exact model and its residual are in [docs/trust-model.md](docs/trust-model.md).)
- **Authorisation failures** — any plugin route reachable without the BTCPay permission it should
  require.
- **Request amplification** against third-party providers through the plugin's own endpoints.
- **Artifact integrity** — anything that lets a modified plugin be installed as though it were the
  released one.

**Out of scope.** These are real risks, and they are documented rather than dismissed, but reports
belong upstream or with the operator:

- **The Breez Spark SDK and its native binaries.** This plugin embeds them; it does not maintain
  them. Report to https://github.com/breez/spark-sdk.
- **The Spark network and its operators.** Funds held on Spark are not in the merchant's sole
  custody until they are swept, and this plugin performs cooperative exits only. That is a property
  of the system being integrated, stated plainly in the README, and the reason automatic sweeping
  exists — it is not a vulnerability in the integration.
- **BTCPay Server core.** Report to https://github.com/btcpayserver/btcpayserver.
- **The merchant's own server.** A compromised host, a stolen BTCPay admin session, or an exposed
  Postgres is outside what a plugin can defend against; anything that can run code on that server
  can reach everything this plugin can. The operator of a *shared* instance is in the same position
  **by design rather than by compromise**: a store set up by a non-admin tenant stores its seed on
  that server, and the instance admin can decrypt it and spend the funds. That is the product model,
  and the setup page warns of it before a seed is created or imported — it is not a vulnerability in
  the integration.
- **The embedded provider API key.** It is a per-application identifier, not a credential, and is
  obfuscated only to keep it out of trivial scrapes. Extracting it is expected and is not a finding.
- Automated-scanner output with no demonstrated impact, missing security headers, and hardening or
  best-practice suggestions with no exploit path.
- Social-engineering and physical attacks.

## Rewards

There is **no bug bounty programme** and no guaranteed payout — this is a small, independently
maintained project, and it would be dishonest to imply otherwise. Valid reports are credited, fixed
promptly, and genuinely appreciated. If a report prevents a real loss of merchant funds, a
discretionary thank-you may be offered, but nothing here should be read as a commitment to pay.

## Supported versions

Only the **latest release** is supported. There are no backported fixes: a security fix ships as a
new version and every previous version should be considered affected. BTCPay does not auto-update
plugins, so upgrading is a deliberate act by the operator — which is why advisories are published
rather than fixes being shipped quietly.

## Prior review

This plugin has had one independent security audit, which reported no critical, high or medium
findings; the fixes for what it did report are in `CHANGELOG.md`. An audit is a snapshot of one
version by one reviewer — it is not a guarantee, and the areas it found thin are recorded in
`docs/limitations.md`. Read that file before deciding how much money to route through this.
