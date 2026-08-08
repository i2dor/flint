using BTCPayServer.Plugins.Flint.Services;
using BTCPayServer.Plugins.Flint.Tests.Fakes;
using Xunit;

namespace BTCPayServer.Plugins.Flint.Tests;

/// <summary>
/// Two BTCPay instances with <em>separate</em> data directories, on one shared database, on one seed.
/// </summary>
/// <remarks>
/// <para>
/// <b>The hazard.</b> <c>SparkStorageLock</c> is a kernel-held claim on a store's storage directory, so it
/// catches two instances sharing one data directory and cannot catch two instances with different ones. In that
/// second shape the plugin's other guard, <c>SparkService._walletOwners</c>, is a per-process dictionary and is
/// equally blind. Two live SDK instances on one wallet is a duplicate-sweep and lost-write hazard, and nothing
/// on either host can see the other. This is the unguarded half of the two-instances hazard, and it was
/// assessed and deliberately left unguarded: BTCPay has no cross-instance coordination of any kind, a lease
/// could not be fenced (no SDK call can be cancelled, so a loser's live handle keeps running whatever a row
/// says), and the topology is not one BTCPay supports.
/// </para>
/// <para>
/// <b>Why it is nevertheless not reachable today, and why that is worth a test rather than a shrug.</b> A
/// store's seed lives in BTCPay's <em>database</em> — which two instances would share — but it is stored
/// encrypted, and the key that decrypts it lives in the <em>data directory</em>, which by construction they do
/// not (<c>Startup.cs:98-100</c>, <c>AddDataProtection().PersistKeysToFileSystem(DataDir)</c>, the only
/// data-protection configuration in the whole of BTCPay). So the second instance reads the row, fails to
/// decrypt it, and never starts a wallet at all. The dangerous topology is blocked by an accident of where
/// ASP.NET keeps its keyring — not by anything this plugin decided.
/// </para>
/// <para>
/// An accident is exactly the kind of guard that gets removed by someone competent doing something reasonable.
/// Persisting the data-protection keyring to the shared database is the standard recipe for making an ASP.NET
/// application run in more than one place, and it is the single change that would turn this from unreachable
/// into automatic — every store, silently, with no operator error involved. So the consequence is pinned here:
/// <b>an instance that cannot decrypt a store's seed must not start that store's wallet.</b> If that ever stops
/// being true, this fails, and whoever changed it has to read §8.3 before deciding it is fine.
/// </para>
/// <para>
/// Note what is deliberately <em>not</em> tested, because it is not implemented and should not be: a
/// cross-process claim. See §8.3 for the argument — in short, BTCPay core would double-pay Lightning payouts
/// in the same topology, and the plugin cannot fence a running SDK instance whatever a database row says.
/// </para>
/// </remarks>
public class SparkSeparateDataDirectoryTests
{
    private const string StoreId = "shared-store";

    [Fact(Timeout = 60_000)]
    public async Task A_second_instance_with_its_own_keyring_does_not_start_a_wallet_on_the_shared_seed()
    {
        // Instance A: the server that configured the store. Its harness has its own data directory, and
        // therefore its own data-protection keyring, exactly as a second container would.
        using var first = SparkServiceHarness.Create();
        // Instance B: a second server, separate volume, reading the same database row.
        using var second = SparkServiceHarness.Create();
        Assert.NotEqual(first.DataDir, second.DataDir);

        var mnemonic = SparkServiceHarness.MnemonicFor(1);
        var paymentKey = SparkConnectionString.GeneratePaymentKey();

        // The row is the *same row* — written by A, read by B. That is what sharing a database means, and it is
        // the whole reason the storage lock cannot help: B's storage directory is untouched and unlocked. Note
        // the mnemonic is protected with A's protector on both sides; only the keyring differs.
        var sharedRow = new SparkSettings
        {
            ProtectedMnemonic = first.Protector.Protect(mnemonic),
            PaymentKey = paymentKey,
            SeedSource = SeedSource.Generated
        };
        first.Stores.Seed(StoreId, Constants.StoreSettingsKey, sharedRow);
        second.Stores.Seed(StoreId, Constants.StoreSettingsKey, sharedRow);

        await first.Service.StartAsync(CancellationToken.None);
        await second.Service.StartAsync(CancellationToken.None);

        // A owns the wallet.
        Assert.Equal([StoreId], await first.Service.GetRunningStoreIds());

        // B does not, and that is the property that keeps two SDK instances off one wallet today.
        Assert.Empty(await second.Service.GetRunningStoreIds());
        Assert.Null(await second.Service.GetClient(StoreId));

        // And B says why, naming the store, rather than failing silently.
        Assert.Contains(StoreId, second.Log.AllText);
        Assert.Contains("seed", second.Log.AllText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact(Timeout = 60_000)]
    public async Task The_same_seed_on_a_second_instance_that_can_read_it_is_not_stopped_by_anything()
    {
        // The negative half, and the one that makes the test above mean something. Give instance B a keyring
        // that can read the row — which is precisely what sharing a data-protection keyring across instances
        // would do — and nothing anywhere refuses. Two live wallets on one seed, no error, no log line.
        //
        // This asserts the *absence* of a guard on purpose. It is the executable form of the decision recorded
        // in this class's remarks — that the other half is not guarded, and cannot be locally — so that the
        // reasoning cannot quietly drift away from the code: implement a cross-process claim and this test
        // fails, which is the correct moment to revisit it.
        using var first = SparkServiceHarness.Create();
        using var second = SparkServiceHarness.Create();

        var mnemonic = SparkServiceHarness.MnemonicFor(1);
        first.SeedStore(StoreId, mnemonic);
        // Seeded through B's own protector, standing in for a shared keyring.
        second.SeedStore(StoreId, mnemonic);

        await first.Service.StartAsync(CancellationToken.None);
        await second.Service.StartAsync(CancellationToken.None);

        Assert.Equal([StoreId], await first.Service.GetRunningStoreIds());
        Assert.Equal([StoreId], await second.Service.GetRunningStoreIds());
    }

    [Fact(Timeout = 60_000)]
    public async Task One_instance_cannot_start_two_stores_on_one_seed()
    {
        // The in-process half, for contrast: within one instance the wallet-owner guard does catch it. Kept
        // beside the tests above so the boundary between what is guarded and what is not is legible in one
        // place rather than inferred from two files.
        using var h = SparkServiceHarness.Create();
        var mnemonic = SparkServiceHarness.MnemonicFor(1);
        h.SeedStore("store-a", mnemonic);
        h.SeedStore("store-b", mnemonic);

        await h.Service.StartAsync(CancellationToken.None);

        Assert.Single(await h.Service.GetRunningStoreIds());
        Assert.Contains("recovery phrase", h.Log.AllText);
    }
}
