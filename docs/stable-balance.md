[← Docs index](README.md)

# Holding the balance in dollars: Stable Balance

Under **Plugins → Flint → Stable Balance**. Off until a merchant turns it on, and **mainnet only** — USDB
has no regtest deployment, and the plugin refuses to store the setting elsewhere rather than accepting it
and silently never converting.

Turning it on converts the store's Bitcoin balance to **USDB**; turning it off converts back. Both run on
Spark's own background worker, both cost a spread, and **neither reports when it finishes** — the SDK has
no event for a conversion at all, so the page shows what the wallet currently reports rather than pretending
to know.

> **USDB is freezable, and that is the point of the disclosure you have to tick.** It is issued by a
> regulated stablecoin issuer, and its token metadata says the issuer can freeze the balance. If they do,
> this plugin cannot move it, sweep it or convert it back. That is a new counterparty *on top of* the Spark
> operators you already depend on.

Two further things worth knowing:

- **It is not a prerequisite for [sweeping to a stablecoin elsewhere](sweeping.md#sweeping-to-a-stablecoin-on-another-chain).** A cross-chain sweep converts Bitcoin
  directly, in one hop with one spread; going through Stable Balance first is two hops with two. It earns
  its place only if you want to *hold* dollars between sweeps.
- **The setting and the wallet can legitimately disagree.** Spark caches the active state per wallet, so
  replacing a store's recovery phrase leaves the new wallet switched off whatever the setting says. The page
  reports the disagreement and offers a button, rather than converting your balance on a page load.
