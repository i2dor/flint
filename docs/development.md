[← Docs index](README.md)

# Local development

## Development loop

Register the plugin with your local BTCPay Server development environment:

```bash
./plugin-register.sh
```

This builds the plugin and writes BTCPay's `appsettings.dev.json` with a `DEBUG_PLUGINS` entry
pointing at the built assembly, so BTCPay side-loads the plugin from your build output instead of
requiring a packaged install:

```json
{ "DEBUG_PLUGINS": "/abs/path/to/BTCPayServer.Plugins.Flint/bin/Debug/net10.0/BTCPayServer.Plugins.Flint.dll" }
```

Start BTCPay's development dependencies:

```bash
cd btcpayserver/BTCPayServer.Tests
docker compose up -d dev
```

Then run BTCPay Server from `btcpayserver/BTCPayServer` (or launch it from your IDE using the
`BTCPayServer: Bitcoin-HTTPS` profile). A breakpoint in `SparkPlugin.Execute` should be hit during
startup. Re-run `dotnet build` and restart BTCPay to pick up plugin changes.

`appsettings.dev.json` is gitignored by BTCPay itself, so registering the plugin does not dirty the
submodule.

## Database migrations

The plugin owns its own Postgres schema (`BTCPayServer.Plugins.Flint`) and applies its migrations
from a startup task. Everything needed to *author* migrations is opt-in behind an MSBuild flag, so
that ordinary Debug and Release builds — and therefore packaged plugins — never carry design-time
packages or duplicate copies of BTCPay's own assemblies:

```bash
dotnet build BTCPayServer.Plugins.Flint/BTCPayServer.Plugins.Flint.csproj -p:EfMigrations=true
cd BTCPayServer.Plugins.Flint
dotnet ef migrations add <MigrationName> --context SparkPluginDbContext --output-dir Migrations --no-build
cd .. && rm -rf BTCPayServer.Plugins.Flint/obj BTCPayServer.Plugins.Flint/bin
```

The final clean matters: the `EfMigrations=true` build leaves BTCPay's assemblies in `bin`, and
loading a plugin folder that contains its own copy of `BTCPayServer.dll` causes assembly-identity
conflicts.

The `dotnet build` above runs a restore, and the repository commits NuGet lock files
(`packages.lock.json` next to each csproj — the pinned dependency graphs that CI restores in
locked mode; see the comment in the csproj files). The design-time restore puts EF design
packages into the plugin's graph, and NuGet updates an existing lock file even with the
`RestorePackagesWithLockFile` property conditioned off — so after authoring a migration, the
lock files will very likely show a diff. That design-time graph must never be committed:
restore the locked files alongside the obj/bin cleanup above (if `git status` shows them clean,
the restore simply did not touch them and there is nothing to undo):

```bash
git checkout -- BTCPayServer.Plugins.Flint/packages.lock.json BTCPayServer.Plugins.Flint.Tests/packages.lock.json
```

No database connection is needed to author a migration — the design-time factory only has to build
a model.
