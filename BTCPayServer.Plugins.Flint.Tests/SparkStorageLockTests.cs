using BTCPayServer.Plugins.Flint.Sdk;
using BTCPayServer.Plugins.Flint.Services;
using BTCPayServer.Plugins.Flint.Tests.Fakes;
using Xunit;

namespace BTCPayServer.Plugins.Flint.Tests;

/// <summary>
/// One live SDK instance per storage directory, enforced across processes rather than only within one.
/// </summary>
/// <remarks>
/// <para>
/// The hazard, measured rather than assumed: with one SDK instance already connected to a storage directory,
/// a <b>second connect on the same directory succeeded in 2 ms</b>, with no error and nothing logged. The SDK
/// takes no lock of its own. The default storage is a single non-WAL SQLite file, so two live instances are
/// two writers on one journal — on the database that holds a wallet with money on it.
/// </para>
/// <para>
/// Within one process <c>SparkService</c> already refuses this, but that guard is a dictionary in memory.
/// Two BTCPay instances against one data directory each pass their own copy of it. These tests cover the
/// claim that closes that gap.
/// </para>
/// </remarks>
public class SparkStorageLockTests
{
    /// <summary>
    /// The mechanism itself: a second claim on a held directory is refused.
    /// </summary>
    /// <remarks>
    /// Taken from a second file descriptor rather than a second process, which the platform treats
    /// identically — <c>flock</c> is per open file description, so two opens in one process conflict exactly as
    /// two opens in two processes do. That is what makes the guard testable at all; the alternative would be
    /// spawning a BTCPay.
    /// </remarks>
    [Fact]
    public void A_directory_that_is_already_claimed_cannot_be_claimed_again()
    {
        using var dir = new TempDirectory();

        var first = SparkStorageLock.TryAcquire(dir.Path, out var firstReason);
        Assert.NotNull(first);
        Assert.Null(firstReason);

        var second = SparkStorageLock.TryAcquire(dir.Path, out var secondReason);
        Assert.Null(second);
        Assert.NotNull(secondReason);

        // The merchant has to be able to act on this, so it must say what is wrong and what to do.
        Assert.Contains("Another process", secondReason);
        Assert.Contains("data directory", secondReason);

        first!.Dispose();
    }

    /// <summary>
    /// A claim the platform itself refuses says what is wrong without saying where on the disk.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A permission refusal surfaces as an <c>UnauthorizedAccessException</c> whose own message
    /// embeds the absolute path of the denied file, and the reason returned here is relayed to
    /// whoever saves the store's settings — a store manager on a <c>CanViewStoreSettings</c>
    /// route, not necessarily a server operator. The host's filesystem layout is a fact about the
    /// server, and it belongs in the operator's log (which names the directory deliberately),
    /// not in the banner.
    /// </para>
    /// <para>
    /// Triggered honestly rather than by mocking: a read-only lock file cannot be opened
    /// <c>ReadWrite</c> on either platform, which is the exact throw. The pre-check skips the
    /// test where the environment does not enforce that anyway — running as root, where the
    /// open would succeed and there is nothing to route.
    /// </para>
    /// </remarks>
    [Fact]
    public void A_claim_the_platform_refuses_names_no_path_to_the_merchant()
    {
        using var dir = new TempDirectory();
        var lockFile = Path.Combine(dir.Path, SparkStorageLock.FileName);
        File.WriteAllText(lockFile, "held elsewhere\n");
        File.SetAttributes(lockFile, FileAttributes.ReadOnly);
        try
        {
            var openSucceeded = false;
            try
            {
                new FileStream(lockFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None).Dispose();
                openSucceeded = true;
            }
            catch (UnauthorizedAccessException)
            {
            }

            Assert.SkipUnless(
                !openSucceeded, "the platform let a read-only file be opened for writing (root?), so the refusal never happens");

            var claimed = SparkStorageLock.TryAcquire(dir.Path, out var reason);

            Assert.Null(claimed);
            Assert.NotNull(reason);
            Assert.Contains("could not be claimed", reason);
            Assert.DoesNotContain(dir.Path, reason);
            Assert.DoesNotContain(SparkStorageLock.FileName, reason);
        }
        finally
        {
            // Restored so TempDirectory can clean the whole thing up.
            File.SetAttributes(lockFile, FileAttributes.Normal);
        }
    }

    [Fact]
    public void Releasing_a_claim_lets_the_next_one_take_it()
    {
        using var dir = new TempDirectory();

        var first = SparkStorageLock.TryAcquire(dir.Path, out _);
        Assert.NotNull(first);
        first!.Dispose();

        var second = SparkStorageLock.TryAcquire(dir.Path, out var reason);
        Assert.NotNull(second);
        Assert.Null(reason);
        second!.Dispose();
    }

    /// <summary>
    /// Two different stores are two different directories, so they never contend.
    /// </summary>
    [Fact]
    public void Claims_on_different_directories_do_not_contend()
    {
        using var one = new TempDirectory();
        using var two = new TempDirectory();

        var first = SparkStorageLock.TryAcquire(one.Path, out _);
        var second = SparkStorageLock.TryAcquire(two.Path, out _);

        Assert.NotNull(first);
        Assert.NotNull(second);

        first!.Dispose();
        second!.Dispose();
    }

    /// <summary>
    /// The claim survives a release that leaves the file behind, because the file is not the lock.
    /// </summary>
    /// <remarks>
    /// Pinned because the tempting cleanup — deleting the lock file on release — is a race: another process
    /// may already hold the same path, and unlinking it would leave two processes each holding an exclusive
    /// claim on a different inode under one name, which is the exact state this guard exists to prevent.
    /// </remarks>
    [Fact]
    public void A_released_claim_leaves_its_file_in_place()
    {
        using var dir = new TempDirectory();

        var held = SparkStorageLock.TryAcquire(dir.Path, out _);
        Assert.NotNull(held);
        held!.Dispose();

        Assert.True(File.Exists(Path.Combine(dir.Path, SparkStorageLock.FileName)));
    }

    /// <summary>
    /// The service refuses to start a wallet whose storage another process is using.
    /// </summary>
    /// <remarks>
    /// The claim is taken <em>before</em> the SDK is connected, which is the only ordering that helps: a
    /// connect that has already happened has already opened the SQLite file.
    /// </remarks>
    [Fact]
    public async Task A_store_whose_storage_is_claimed_elsewhere_does_not_start_its_wallet()
    {
        using var h = SparkServiceHarness.Create();
        h.SeedStore("contended", SparkServiceHarness.MnemonicFor(4));

        // Stands in for the other BTCPay instance, which took the claim first.
        var other = SparkStorageLock.TryAcquire(h.StorageDirFor("contended"), out _);
        Assert.NotNull(other);

        await h.Service.StartAsync(CancellationToken.None);

        Assert.Null(await h.Service.GetClient("contended"));

        // And the SDK was never even asked to connect — the point is to refuse before a second writer opens
        // the file, not to close one afterwards.
        Assert.DoesNotContain("contended", h.Sdk.Connects);

        Assert.Contains("Another process is already using", h.Log.AllText);

        other!.Dispose();
    }

    [Fact]
    public async Task A_contended_store_does_not_stop_another_stores_wallet_from_starting()
    {
        using var h = SparkServiceHarness.Create();
        h.SeedStore("contended", SparkServiceHarness.MnemonicFor(4));
        h.SeedStore("free", SparkServiceHarness.MnemonicFor(5));

        var other = SparkStorageLock.TryAcquire(h.StorageDirFor("contended"), out _);
        Assert.NotNull(other);

        await h.Service.StartAsync(CancellationToken.None);

        Assert.NotNull(await h.Service.GetClient("free"));
        Assert.Null(await h.Service.GetClient("contended"));

        other!.Dispose();
    }

    /// <summary>
    /// Shutting a store's wallet down releases its storage for whoever wants it next.
    /// </summary>
    /// <remarks>
    /// The half that a lock is easy to get wrong on. A claim never released turns a restart-in-place, a
    /// reconfigure, or a blue/green swap into a permanently dead wallet — a self-inflicted outage in the name
    /// of preventing a hypothetical one.
    /// </remarks>
    [Fact]
    public async Task Tearing_a_wallet_down_releases_its_storage()
    {
        using var h = SparkServiceHarness.Create();
        h.SeedStore("store-a", SparkServiceHarness.MnemonicFor(6));

        await h.Service.StartAsync(CancellationToken.None);
        Assert.NotNull(await h.Service.GetClient("store-a"));

        // While it runs, nothing else can have the directory.
        Assert.Null(SparkStorageLock.TryAcquire(h.StorageDirFor("store-a"), out _));

        await h.Service.StopAsync(CancellationToken.None);

        var afterStop = SparkStorageLock.TryAcquire(h.StorageDirFor("store-a"), out _);
        Assert.NotNull(afterStop);
        afterStop!.Dispose();
    }

    /// <summary>
    /// Reconfiguring a running store must not deadlock against its own claim.
    /// </summary>
    /// <remarks>
    /// <c>Set</c> replaces the instance by tearing the old one down and connecting a new one. If the claim
    /// were released after the new one was taken rather than before, the store would refuse to restart itself
    /// — every settings save would take the merchant's wallet down.
    /// </remarks>
    [Fact]
    public async Task Reconfiguring_a_running_store_reclaims_its_own_storage()
    {
        using var h = SparkServiceHarness.Create();
        h.SeedStore("store-b", SparkServiceHarness.MnemonicFor(7));

        await h.Service.StartAsync(CancellationToken.None);
        var before = await h.Service.GetClient("store-b");
        Assert.NotNull(before);

        var settings = await h.Service.Get("store-b");
        Assert.NotNull(settings);
        var applied = await h.Service.Set("store-b", settings);

        Assert.True(applied.WalletRunning, applied.Reason ?? "the wallet did not come back up");
        Assert.NotNull(await h.Service.GetClient("store-b"));
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "spark-lock-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
