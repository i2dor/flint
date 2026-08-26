<!--
  Prepended to GitHub's auto-generated notes by .github/workflows/package.yml. A release page is
  where someone decides whether to trust an 80 MB binary that will hold their store's Lightning
  keys, so it has to tell them how to check it rather than assume they will find the README.

  The two placeholders below are substituted with the repository slug and the tag by the workflow,
  which also strips this comment. Nothing else is templated, so this file can be edited and
  reviewed as ordinary Markdown.
-->
## Verify before you install

Every artifact here is signed with GitHub build provenance — a Sigstore attestation binding the
file's digest to this repository, this workflow, and the commit that built it. There is no
maintainer key to fetch and no trust-on-first-use step.

```bash
gh attestation verify BTCPayServer.Plugins.Flint.btcpay --repo __REPO__
```

Offline, against the `attestation.jsonl` bundle attached below rather than GitHub's API:

```bash
gh attestation verify BTCPayServer.Plugins.Flint.btcpay \
  --bundle attestation.jsonl --repo __REPO__
```

`SHA256SUMS` is attested too, so it can be trusted once verified. One quirk: BTCPay's PluginPacker
writes it with a single space between hash and filename. GNU `sha256sum -c` accepts that; macOS's
`shasum -a 256 -c` rejects the whole file, so on macOS compare `shasum -a 256 <file>` by eye.

## Installing

**Server settings → Plugins**, find **Flint** in the plugin store
([official listing](https://plugin-builder.btcpayserver.org/public/plugins/flint)), and install it.
BTCPay restarts itself to finish.

Alternatively, **Server settings → Plugins → Upload plugin**, and select
`BTCPayServer.Plugins.Flint.btcpay`. BTCPay restarts itself to finish. Both routes require BTCPay Server
2.4.1 or newer, on a non-Alpine host.

Read [CHANGELOG.md](https://github.com/__REPO__/blob/__TAG__/CHANGELOG.md) for what is in this
release and how far it has actually been proven, and the
[trust model](https://github.com/__REPO__/blob/__TAG__/docs/trust-model.md), before you put money
through it.
