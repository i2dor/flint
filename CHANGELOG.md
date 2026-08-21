# Changelog

All notable changes to this plugin are recorded here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

The version in [`BTCPayServer.Plugins.Flint.csproj`](BTCPayServer.Plugins.Flint/BTCPayServer.Plugins.Flint.csproj)
is the single source of truth, and `PluginVersionTests` asserts that it matches the newest heading
below — so a release that forgets this file fails the test suite.

## [Unreleased]

### Security

Findings from the v0.1.4 external security review (a three-model quorum over the release tag), each verified
against the source before fixing:

- **A sweep write-off can no longer unblock a re-sweep on stale storage.** After the five-minute grace, a
  pending sweep the SDK had no payment for was written off as never sent — but that lookup ran before the
  pass's forced sync, and an SSP-accepted exit the SDK had not replayed locally yet looks exactly like
  "never sent". The write-off now forces an explicit wallet sync and repeats the lookup first, and for
  sats-funded rows it additionally refuses to close the row while the synced balance no longer holds the
  amount the sweep would have sent — the shape that suggests the exit actually happened. A row the gate
  refuses keeps blocking new sweeps, which is the safe direction.
- **A cross-chain send now checks the provider's echoed recipient against the requested destination.** The
  prepared payment's recipient is an echo from the provider, and every guard downstream is amount-shaped —
  none of them would have noticed the money going to the right chain at the wrong address. A mismatch
  refuses the send.
- **The cross-chain value guard refuses amounts too large to value, instead of skipping itself.** The
  base-unit-to-dollar conversion reported an overflowing value as zero, and the guard read zero as "already
  refused upstream" — so the most absurdly sized quotes were exactly the ones that bypassed the value check.
  Overflow is now an explicit refusal.
- **The payment key is rotated on every provision.** The connection string is a bearer spend credential;
  re-provisioning previously carried the old key over, so every previously issued copy of the string could
  drive the new wallet. Setting Spark up again now mints a fresh key and rewrites the store's Lightning
  configuration with it in the same operation — which also makes re-running setup the way to revoke a
  leaked string.
- **A wallet that has not reported its identity yet bypasses the deposit-address cache.** The cache was
  keyed on an empty identity like any other, so after a seed change a request racing the new wallet's first
  sync could be handed the previous wallet's deposit address. An unknown identity now always fetches a live
  address and caches nothing.
- **The plugin's directories are owner-only from the instant they exist.** Storage and log directories were
  created at the process umask (0755) and restricted to 0700 afterwards, leaving a window in which what
  landed there was world-readable; the mode is now passed to the creation itself, and the storage lock file
  is created owner-only too.

Two follow-ups from the quorum's review of the fixes themselves:

- **The write-off shortfall gate is bounded, not absolute.** The gate's observation — funds missing with no
  payment record — is also produced by a Lightning payout or a Stable Balance conversion landing near a
  sweep that genuinely never went out, and sweeps are close enough to the whole balance that any spend trips
  it. Unbounded, that coincidence would wedge the store's sweeping permanently with no operator escape; the
  gate now blocks for an hour of synced re-checks and then writes the row off with a reason stating exactly
  what was observed and what to verify.
- **A provisioning rollback now also covers a throwing Lightning-configuration write.** With the key
  rotating, settings carrying a new key beside a configuration still holding the old string is a store whose
  checkout fails; a throw out of the wiring write now restores the previous settings the same way a refused
  write always did. A process crash inside that window is still detectable and repairable from the status
  page, which inspects the wiring against the stored key.

### Changed

- **The Spark SDK is now 0.22.3**, up from 0.22.2, on the review's recommendation: the one upstream change
  ("stop cross-chain sends claiming their own outgoing transfer") is an accounting fix directly on the
  cross-chain rail this plugin uses.

## [0.1.4] — 2026-08-21

### Changed

- **The Spark SDK is now 0.22.2**, up from 0.22.0. A patch-range bump with, as with the last bump, no
  release notes published upstream. No API surface change reached this plugin: the build and every test
  suite — including the live and funded regtest suites against the real SSP — passed without a single
  call-site change, and the build ran on both test servers before this release was cut.
- **Sweep labels validate the provider's transaction id before writing it to the wallet.** The txid that
  labels a sweep comes from the Spark provider's payment data, so it is now checked as a well-formed Bitcoin
  transaction id (64 hex characters) — the same guard the plugin already applies to externally-supplied
  payment hashes — before it becomes a wallet object and a rendered `flint-sweep` label. A malformed id is
  skipped and logged, never thrown, keeping the labeler's best-effort contract total: a bad provider value
  cannot abort a reconciliation pass or attach a false "swept from this store's Spark wallet" provenance
  label to an unrelated transaction in the store's wallet. Cross-chain deliveries remain unlabelled and skip
  this check entirely.

## [0.1.3] — 2026-08-17

### Added

- **Sweeps are labelled in the store's Bitcoin wallet.** Once a sweep's transaction id is known — at send,
  or when crash reconciliation resolves it — the plugin writes a `flint-sweep` label onto the transaction in
  the store's BTCPay wallet, the same way core labels payouts and invoices. The wallet's transactions list
  then says where the money came from, with a tooltip, filterable like any other label. Cross-chain sweeps
  are not labelled, because their delivery never appears in a Bitcoin wallet.

### Changed

- **Deposits moved off the navigation, behind Advanced.** A Spark wallet is funded by customers paying
  invoices — no merchant needs to send their own funds in for the plugin to work — so the deposits page is
  now reached from the Advanced page instead of carrying a top-level entry. The status page still flags a
  stuck deposit loudly and links straight to the page that fixes it.

## [0.1.2] — 2026-08-17

A UI pass ahead of publicising the plugin: fewer words, and a page for the things most stores never touch.

### Changed

- **The navigation gained *Deposits* and *Advanced* entries, and now collapses.** The sub-entries render
  only while you are inside the Flint section, the way core's own store-settings menu behaves, instead of
  following you around the rest of the store. The status page's "Advanced" accordion is gone — it was not
  obviously expandable, and it was carrying real pages.
- **A new Advanced page** holds the recovery phrase's provenance, seed replacement, the Spark identity,
  the SDK storage path, wallet removal, and the two sweep settings almost nobody should change: the
  reserve ("Leave behind") and the fee policy ("Take the exit fee out of the swept amount"). All of it
  moved off the status and sweep pages; nothing changed in what is stored or in the Greenfield API.
- **The status page now leads with the balance** and the stuck-deposit alert links straight to the
  Deposits page. The recovery-phrase row, the wallet-details accordion and the removal button moved to
  Advanced, and the "indicative balance" footnote is gone.
- **The deposits page stopped printing its title twice** and is reachable from the navigation.
- **Form notes were cut back across the plugin.** The confirmation-speed field now shows roughly what
  each tier pays in sat/vB right now, read from the same mempool feed the deposits page uses, instead of
  a sentence about tiers.

## [0.1.1] — 2026-08-14

A dependency-only release. No plugin source changed between 0.1.0 and this; the packaged artifact
differs from 0.1.0 only in the Spark SDK's bundled native libraries. Upgrading is an ordinary plugin
update — the identifier, the settings key, the Postgres schema, the SDK storage directory and the
data-protection purpose are all untouched, so **no recovery phrase needs re-importing** and no
balance has to be swept out first. The 0.1.0 migration warning applies to arriving from the
predecessor plugin, not to this step.

### Changed

- **The Spark SDK is now 0.22.0**, up from the 0.19.2 that 0.1.0 shipped. Breez published no release
  notes across that range, so the bump was reviewed by reading the diff of the surface instead: it is
  additive — batch sends, CPFP, prepared unilateral exit, passkeys, none of which this plugin calls —
  with one breaking change that reached us. `SdkException.InsufficientFunds` stopped being a
  payload-free variant and now carries the identifier of whichever balance was short, which broke two
  test call sites and no production code. `SparkErrors.IsInsufficientFunds` classifies a token
  shortfall the same as a sat shortfall, because the plugin only ever spends sats and would otherwise
  report a token error as "state unknown" — the classification that makes a sweep unsafe to retry.
  The reason to ship a bump with no known fix in it is that the SDK is pre-1.0 and releases roughly
  monthly: staying near the tip keeps each upgrade a small step that can be read in an afternoon,
  rather than a large one taken later under pressure.
- **The plugin is built against BTCPay Server 2.4.2**, up from 2.4.1. This is the release it is
  compiled and tested against, not a new requirement: the declared support floor is unchanged at
  2.4.1, so every host that can run 0.1.0 can run this.

## [0.1.0] — 2026-08-08

The first release under the name **Flint**, and the first from this repository.

The code is not new. It was built and independently audited under a previous name and owner, and this
release is that work with the branding, licence and plugin identity changed. The sections under
*Earlier development* below record what happened before the rename; their version numbers were never
released and do not correspond to anything published here.

> **Migrating from the predecessor plugin?** The plugin identifier changed, so BTCPay treats this as a
> different plugin: uninstall the old one and install this. **You must re-import your store's recovery
> phrase.** Every constant that keys stored data moved with the rename — the settings key, the Postgres
> schema, the SDK storage directory, the Lightning connection-string type, and the data-protection
> purpose the phrase is encrypted under. Re-importing restores the wallet and its balance from the
> network; it does not restore local payment, payout or sweep history, which stays with the orphaned
> schema. Sweep everything out and settle any in-flight payment before you switch, because the
> idempotency records that stop a sweep being sent twice do not survive either.


### Added

- **Spark has sections in the store navigation** instead of a single entry. *Sweeps* and *Stable
  Balance* (mainnet only) are reachable directly, rather than by finding the right button on the status
  page. Deposits and removal deliberately stay off the nav: a Spark wallet is funded by customers paying
  invoices, so a merchant depositing by hand is the exception, and removal is destructive and belongs
  next to the state it destroys. Sub-entries appear only once the store has a wallet, because before
  that both destinations redirect to setup and would be three ways of reaching one page.
- **Setup can turn sweeping on.** Step 2 used to be a paragraph about sweeping and an assurance you
  could configure it afterwards, which put the safest configuration — not leaving a growing balance on
  a second layer — behind an extra trip to another page. It now asks the only two questions that decide
  whether sweeping happens at all: on or off, and the threshold. Destination, fee limits, minimum and
  confirmation speed keep their defaults on the sweep page. It is applied after provisioning and never
  before, and a failure there does not fail setup — the wallet is up and the usual cause is a store with
  no on-chain wallet to sweep into. But it is not silent: the reason rides along in the success message,
  so nobody reads "Spark is now set up" and believes a balance is being swept when none is.

### Changed

- **The plugin identifier is now `BTCPayServer.Plugins.Flint`.** ⚠️ **Operators must uninstall the
  old plugin, install this one, and re-import the store's recovery phrase** — BTCPay keys an install by
  the identifier, so it sees this as a different plugin, and every constant that keys stored data moved
  too. The migration warning at the top of this release is the authoritative list of what carries over
  (the wallet and its balance, from the network) and what does not (local history and idempotency
  records).
  The reason for the change is that a third party had already registered `BTCPayServer.Plugins.Spark` on
  the official plugin registry, and BTCPay joins an installed plugin to a registry entry by identifier
  alone: their repository was credited as this plugin's author, the card's "Sources" and "Details" links
  pointed at their code, and their build would have been offered as an update to this one as soon as
  their version passed ours. It also meant this plugin could never be listed under its own name.
- **The plugin's own page no longer prints its title twice.** `vc:title-header` renders a breadcrumb
  trail and then the title, and synthesises a trail from the title when a page sets none — so the status
  page showed "Flint" above "Flint". The status page is the plugin's root and has no parent to
  point at, so it renders the heading alone; the Sweeps, Stable Balance and removal pages now set a trail
  back to it, which is where a breadcrumb earns its place.
- **The plugin is called "Flint".** It is built and maintained by Seth For Privacy (see `LICENSE`), and
  a plugin calling itself plain "Spark" implies it comes from Spark or from Breez. The name appears in
  the manifest, the nav entry, the status page and the Lightning connection's label. Mentions of the
  Spark *network* — "Spark wallet", "Spark balance", "Spark sweeps" — are still correct and are
  unchanged: that is the network the plugin connects to, not what the plugin is called.
- **The nav entry carried the previous owner's logomark with a Spark asterisk**, instead of BTCPay's
  generic plugin symbol. (The mark has since been replaced by the Flint mark; the mechanics are
  unchanged.) It is an inline `<svg>` because `<vc:icon>` can only address symbols in core's own
  sprite, and it inherits `currentColor` so it survives dark themes and the nav's hover and active states.
- **The README and registry logo became a real mark**, in the same composition as the nav icon,
  replacing the placeholder drawn for the repository. It is a raster on backgrounds nobody controls
  rather than themed markup, so it carries its own colours instead of inheriting one. (That mark
  belonged to the previous owner and has since been replaced by the Flint mark.)
- **Cross-chain sweep destinations are read from the provider at runtime** instead of a hardcoded list of
  six chains and USDT. The provider carries thirteen EVM chains reachable from Spark and USDC on eleven
  of them, and says in as many words not to hardcode this. What the static list was missing was not a
  rounding error: `base`, `avalanche`, `monad`, `hyperevm` and `sei` were absent entirely, and USDC — the
  most widely carried asset in the table — was absent from every chain that was present. The catalogue is
  cached rather than fetched per render (six hours after a success, five minutes after a failure), no
  render ever waits on the network, and at most one fetch happens per interval however many requests
  arrive.
- **The chain and asset fields on the sweep page are pickers, not free text.** The two fields that decide
  which chain a store's money lands on were the two you could typo — the chain was free text with a
  datalist of suggestions, and the asset was free text with nothing at all. The asset list is derived
  from the selected chain. Nothing in the picker authorises a route: `CrossChainRouteResolver` re-reads
  the live table before every send.
- **The status page leads with the balance**, as two figures rather than a table row: sats always, and
  the stablecoin holding beside it when Stable Balance is on. Those are two different assets rather than
  two views of one, and setting them side by side is what makes that legible. Deliberately not a chart —
  the plugin stores no balance history, and the figure is read live from the SDK each time, so anything
  time-shaped would be invented rather than measured.
- **Spark identity, the SDK storage path and the on-chain deposit address moved into a collapsed
  Advanced section** on the status page, and the Spark network section is gone. The uncredited-deposit
  alert stays on the page unconditionally: that one is money the merchant sent that did not arrive.
  Merchant-facing copy also loses every "on regtest we measured" aside — those were notes to ourselves
  about how the numbers were obtained, and the substance survives where it changes a decision.
- **The plugin-list description is about 300 characters, down from 613.** It read like the README's opening
  section, which is the wrong job for a line sitting in a list beside a dozen other plugins, and it now
  describes Spark as a non-custodial layer two rather than naming the operator threshold. The operator
  count goes stale as operators are added, and a plugin-list entry is the worst place to carry a fact
  with a shelf life; the README, trust model and Stable Balance page still name the specifics, which is
  where someone goes to check them.

### Fixed

- **The seed-leak guards no longer fail at random.** Both tests that prove recovery-phrase material never
  leaves the server compared the phrase word by word against text that is ordinary English, so they
  collided with it. One matched `"word"` against raw JSON and could not tell a key from a value, so a
  generated phrase containing "history" hit `SparkSweepConfigurationData`'s own `history` property. The
  other matched `" word "` against the operator log, whose success line ends "…configured from a generate
  seed" — and "seed" is a BIP39 word, so roughly one provisioning in a hundred and seventy failed a test
  with no leak in it. Both now look for two *consecutive* words, which prose does not produce by accident
  and no real leak can avoid. This matters more than an ordinary flake: a security guard that fires at
  random teaches everyone to re-run it, so the first true failure gets dismissed as the flake.

## Earlier development

The two sections below predate the rename and were never released under any name. They are kept
because they are the honest provenance of this code, including the security audit and its fixes.

### Audit fixes (previously numbered 0.2.0)

Fixes from an independent security audit of `90bf6ee` (2026-08-07). Two of these move money, so this
supersedes 0.1.0 outright — do not ship 0.1.0.

### Fixed

- **A sweep whose SDK handle was disposed mid-send could be swept twice.** `IsProvablyNotSent` never
  mentioned `ObjectDisposedException`, but it tested `InvalidOperationException`, which disposal derives
  from — so a disposed handle was classified as "nothing was sent", the record resolved `Failed`, and the
  store was free to sweep the same balance again on the next pass while the first send may already have
  left the wallet. Disposal races a send on every reconfigure and shutdown. The generic catch's own
  comment always named a disposed handle as a genuinely unknown outcome; now it actually reaches it.
- **Two concurrent payments of one invoice both reported success.** `Pay` probes the SDK for an earlier
  send and then sends, with nothing serialising the two, so two callers — the automated payout processor
  ticking while someone confirms by hand, or two Greenfield pay calls — could both pass the probe and
  both send under the same idempotency key, marking two payouts `Completed` against one payment. The
  probe and send are now one critical section per invoice.
- **A blip before anything was sent got the payout cancelled.** Failures in the idempotency probe and in
  `PrepareSendPayment` both ran before `SendPayment` and provably spent nothing, but were reported as
  `Unknown`; BTCPay then marked the payout `InProgress` and cancelled it ten minutes later. They are now
  reported as a definite error, which returns the payout for an immediate, safe retry.
- **A hung SDK `Disconnect` wedged the whole plugin.** Teardown runs inside the process-wide instance
  lock, so an unbounded await there blocked every later setup save, every store deletion and host
  shutdown itself until the process was killed. It is now bounded like the connect already was, and the
  handle is disposed either way.
- **A permanently hung connect locked a store out of its own wallet until BTCPay restarted.** The
  abandoned connect held the store's storage lock while awaiting a task nothing can cancel, and the
  store's next attempt then failed with a message blaming another process for a hold this one was doing
  to itself. The lock is now released after a grace period.

### Changed

- **A settlement for less than the invoiced amount is now logged as a warning.** Nothing compared the
  arrived amount to the invoiced one, and the record settles once and never revises upward. This is a
  loud warning rather than a refusal on purpose: refusing on an amount the SDK reported slightly low
  would stop legitimate invoices settling, which is worse than the unproven Spark-rail case it would
  defend against.

### Documentation

- **The Lightning connection string is documented as a bearer spend credential**, in
  `docs/trust-model.md` and in `SparkConnectionStringHandler`'s own remarks. The previous claim that
  store binding "closes the cross-store wallet-hijack hole" was too strong: it closes the
  key-without-a-store-id case, but copying a whole string onto another store on the same server still
  drives the original's wallet — confirmed live by the audit. The key is also never rotated.
- **The status page no longer claims checkout will fail** when a store points at another store's Spark
  wallet. Checkout succeeds and the money goes to the other store, which is the thing worth saying.

### Initial implementation (previously numbered 0.1.0)

First release. Nothing shipped before it, so this is a description of what exists rather than a
diff, and it is deliberately as long on limitations as it is on features.

### What it does

- **Nodeless Lightning receive.** Registers a `breezspark` Lightning connection-string handler and a
  per-store `ILightningClient` backed by the [Breez Spark SDK](https://sdk-doc-spark.breez.technology/)
  running in-process. A store receives Lightning payments with no Lightning node, no channels and no
  inbound-liquidity management.
- **One-page setup, no connection string to copy.** Under *Plugins → Spark*, or from the store's
  *Connect to a Lightning node* screen. The plugin writes the store's `BTC-LN` and `BTC-LNURL`
  payment-method configuration itself, so LNURL and Lightning addresses work through BTCPay core as
  soon as setup finishes. Seed choice is: generate a new BIP39 phrase (default), reuse the store's
  hot-wallet phrase (offered only when BTCPay actually holds one, with the passphrase caveat spelled
  out on the page), or import an existing phrase. All three sit behind BTCPay's hot-wallet gate.
- **Seeds encrypted at rest and never rendered back.** The phrase is protected with `IDataProtector`
  before storage; there is no reveal-seed feature and no API that reads one out. A generated phrase
  is shown exactly once.
- **Settlement that does not trust the event stream.** The SDK's stream has been observed dropping a
  completion and firing a duplicate 57 ms apart on two threads, and BTCPay does not re-poll pending
  invoices. So the plugin runs its own reconciliation pass (at startup and once a minute) over every
  unpaid invoice, oldest first, and keeps looking for one hour past expiry because the service
  provider still accepts late payments. Settlement is a database compare-and-set, so a duplicated
  event, a `GetInvoice` lookup and a reconciliation pass racing each other produce exactly one credit.
- **Auto-sweep to Bitcoin, by cooperative exit only.** Threshold-triggered (checked every two
  minutes) or on demand via *Sweep now*, which goes through the identical engine with the identical
  server-side guards. Destinations: a fresh labelled address reserved from the store's own BTC
  derivation scheme and rotated per sweep, or one fixed address validated against this server's
  chain on save and again before every send.
- **A fee guard that cannot be switched off.** Percentage-based rather than flat, because a flat cap
  set today refuses every sweep the first time mainnet broadcast fees rise past it. Clearing it falls
  back to the default; whatever is configured, the plugin will not pay more than half of what a sweep
  delivers.
- **Crash-safe sweeps.** A `SweepRecord` with a fresh UUID is written *before* the SDK call, and the
  SDK adopts that UUID as the payment id, so the next pass can ask `GetPayment(key)` for a definitive
  answer. Nothing is retried blind, and no new sweep starts while an earlier one's fate is unknown.
- **Refusals are recorded, not just logged.** The history page answers "why has nothing swept?".
  Recurring automatic refusals fold onto one row keyed on the *kind* of refusal, with a count and a
  last-seen time, bounded to a day so a condition that stopped and came back reads as two episodes.
- **On-chain deposits that actually arrive.** The wallet's static deposit address, plus the three
  things that keep a top-up from silently stranding: a claim-fee ceiling expressed as the
  network-recommended rate plus a leeway rather than Spark's fixed 1 sat/vB default, a display of
  what is stuck and what fee it needs, and a one-click manual claim guarded by a per-store ceiling
  and by a backstop that refuses to spend more than half a deposit on claiming it. That last guard
  binds on the manual path only — see the maturity note below on automatic claiming.
- **Stable Balance (mainnet only).** Optionally hold the store's balance in USDB between sweeps.
  Off by default, gated behind an explicit acknowledgement of the freezability disclosure, and
  refused outright on non-mainnet rather than accepted and silently never converted.
- **Cross-chain sweeps (mainnet only).** A third destination: a stablecoin delivered to an address
  you control on an EVM chain, through a bridge provider, still as an ordinary cooperative Spark
  transfer at the point money leaves the wallet. EIP-55 checksums are verified where present, the
  asset is matched exactly (`USDT0` is not substituted for `USDT`), and the provider's quote is
  sanity-checked against the store's own exchange rates — a sweep losing more than 10% of its value
  is refused, as is one that cannot be checked at all.
- **A Greenfield API covering everything the pages do**, over ten endpoints under
  `/api/v1/stores/{storeId}/spark`, scoped to the usual store view/modify permissions and documented
  in the server's own `/docs` and `swagger.json`. It is a second surface over the same services, not
  a second implementation.
- **Operational hardening.** Per-store SDK state and the SDK log directory are created `0700` and
  re-hardened on every start; an exclusive claim on each store's storage directory refuses to start a
  second wallet on it rather than putting two writers on one SQLite file; the SDK's Rust log is
  scrubbed of credential-shaped values on its way into BTCPay's log, and a `trace` filter — at which
  the provider's session token is logged in full — is refused.
- **Its own Postgres schema and migrations**, applied from a startup task, with design-time tooling
  behind an opt-in MSBuild flag so no packaged plugin carries it.

### Maturity — read this before putting money through it

This release is **thinly proven**. It is feature-complete for what it sets out to do and it has been
run on mainnet, but "run on mainnet" means something narrower than it sounds:

- **One mainnet happy-path run per money path, and no more.** A real store delivered one full
  BTC → USDB → USDT → Arbitrum cross-chain sweep whose on-chain amount matched the plugin's recorded
  figure exactly, one cooperative-exit sweep to Bitcoin, and one on-chain deposit auto-claim at live
  fee rates. Single-digit dollar amounts, one operator, one happy path each. That establishes that
  the plumbing connects and that the figures the plugin reports are truthful. It establishes nothing
  about volume, concurrency, or any adverse condition.
- **Every recovery path is unexercised against the real provider.** A crashed cross-chain send, a
  stuck conversion, a refund, an expired fee quote mid-send — all of these are covered only by unit
  tests against a fake SDK deliberately built to model the real one's hazards. None has been made to
  happen for real.
- **Neither post-MVP feature can be tested off mainnet at all.** Cross-chain sending is hard-gated
  (the SDK throws at connect on any other network) and Stable Balance is accepted on regtest and then
  never converts, because USDB does not exist there. So neither has CI coverage against a live
  service, on any network.
- **The Orchestra bridge provider is a single point of failure for cross-chain sweeps.** It is the
  only provider that works today: every Boltz route currently fails when the send is prepared, so the
  plugin filters them out and reports a Boltz-only destination as having no route. If Orchestra stops
  routing, cross-chain sweeping stops, with no fallback.
- **USDB is issuer-freezable.** Holding the store's balance in USDB means holding a token whose
  metadata says its regulated issuer can freeze it. If they do, this plugin cannot move it, sweep it
  or convert it back. That is a named counterparty in a jurisdiction, *in addition to* the Spark
  operators, not instead of them.
- **Automatic deposit claiming is bounded by fee *rate*, not by a share of the deposit.** The
  "refuses to spend more than half a deposit on claiming it" backstop above binds on the *manual*
  claim path only. Automatic claiming is the SDK's own background worker: there is no per-deposit
  hook and no callback the plugin could refuse from, and the whole of the plugin's influence is the
  single `maxDepositClaimFee` handed over at connect — whose three available shapes (flat sat cap,
  flat rate, recommendation plus leeway) are all amount-blind. So a small deposit that matures during
  a fee spike can still lose a large share of itself to its own claim. Closing this properly means
  taking automatic claiming away from the SDK and claiming every matured deposit from the plugin
  through the guarded path, which is a real design but not one to ship without a mainnet run behind
  it — a mistake there strands every deposit rather than overpaying on one.
- **All cooperative-exit fee defaults are regtest measurements** from a chain at 1 sat/vB. Every real
  decision uses a live quote, but the shipped defaults will very likely refuse every sweep on
  mainnet until the threshold and minimum are raised by an order of magnitude. That is the intended
  failure direction — refuse rather than overpay — but it means the defaults are a starting point,
  not a recommendation.
- **Funds on Spark are not in your sole custody.** Spark is a 2-of-3 statechain operated by
  Lightspark, Breez and Flashnet, and this plugin performs cooperative exits only — it offers no
  unilateral-exit path anywhere in its UI or its code. If the operators became unavailable, recovery
  would mean taking the store's recovery phrase to another Spark wallet implementation. Sweeping is
  the only thing that reduces this exposure.

See **[Known limitations](docs/limitations.md)** for the full list, including the LUD-06
description-hash gap, unsupported platforms (`linux-musl`, `win-arm64`) and networks (testnet,
signet), the unrotated `sdk.log`, and the one SDK error classification with no automated coverage.

### Requirements

- BTCPay Server **2.4.1** or newer (declared support floor; the plugin is compiled against 2.4.1).
- A host on `linux-x64`, `linux-arm64`, `osx-x64`, `osx-arm64`, `win-x64` or `win-x86`. Standard
  Debian-based BTCPay Docker images are fine; Alpine is not, and neither is `win-arm64`.
- Mainnet or regtest. Testnet and signet are not supported by the SDK.
- Server-admin rights, or the *Non-admins can create Hot Wallets for their Store* policy, since
  Spark keeps keys on the server.

[0.1.0]: https://github.com/sethforprivacy/flint/releases/tag/v0.1.0
