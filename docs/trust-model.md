[← Docs index](README.md)

# Trust model

Spark is a 2-of-3 statechain system operated by Lightspark, Breez and Flashnet. Funds held on Spark
are not held in your own custody in the way an on-chain UTXO or a Lightning channel you own is:
every Lightning receive rides Lightspark's service provider, and unilateral exit is a multi-day last
resort that requires reachable operators and an external UTXO. Keeping the auto-sweep threshold low
is the best available mitigation, since it bounds how much is ever exposed on the L2.

The plugin performs **cooperative exits only** (owner decision). It offers no unilateral-exit path, so if the
Spark operators were to become unavailable or refuse to process an exit, recovering funds would mean using
the store's recovery phrase with another Spark wallet implementation. Set the sweep threshold according to
how much you are willing to have depend on those operators; sweeping is the only thing that reduces it.

**Stable Balance adds a second counterparty, and a different kind.** Holding the store's balance in USDB
means holding a token issued by a regulated stablecoin issuer whose metadata says it is **freezable**: the
issuer can freeze the balance, and if they do, this plugin cannot move it, sweep it or convert it back. That
is not the same risk as the statechain operators — it is a named party subject to a jurisdiction — and it is
in addition to them, not instead. The feature is off by default and cannot be enabled without acknowledging
it, on the settings page and through the API alike.

**A cross-chain sweep adds a bridge provider** for the duration of the send. The funds leave the Spark
wallet as an ordinary transfer to the provider's address and depend on the provider to settle on the far
side; until it does, they are neither on Spark nor at the destination. The plugin records the provider's own
quote id before sending and reports what it says it delivered, which is the most the SDK exposes.

**The store's Lightning connection string is a bearer spend credential.** Setup writes a
`type=breezspark;store-id=…;key=…` string into the store's Lightning payment method. Anyone who can read it
— any principal with `CanModifyStoreSettings` on the store, plus anything that string was ever pasted into —
can save it on *another* store on the same server and drive this store's wallet from there: receive into it
and spend from it. The embedded store id binds the key to a wallet; it does not bind the string to the store
it was saved on, because BTCPay's `ILightningConnectionStringHandler` never tells a handler which store is
being configured. This is the same property an LND macaroon has, with one difference worth knowing: the
plugin generated this credential for you rather than you choosing to issue it, and it is **not rotated** when
Spark is re-provisioned, so it outlives the access of whoever saw it and is invalidated only by removing
Spark from the store and setting it up again. Treat it like a macaroon, and keep the sweep threshold low
enough that the balance it could reach is a balance you can afford to lose.
