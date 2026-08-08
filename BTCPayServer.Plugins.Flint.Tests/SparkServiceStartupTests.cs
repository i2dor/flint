using System.Diagnostics;
using BTCPayServer.Plugins.Flint.Services;
using BTCPayServer.Plugins.Flint.Tests.Fakes;
using Xunit;

namespace BTCPayServer.Plugins.Flint.Tests;

/// <summary>
/// <c>SparkService.StartAsync</c> must always finish, whatever a store's wallet does.
/// </summary>
/// <remarks>
/// <para>
/// The host gives it no help. <c>Program.Main</c> reaches this through
/// <c>IHost.StartAsync</c> → <c>IEnumerable&lt;IHostedService&gt;</c>, and
/// <c>HostOptions.StartupTimeout</c> is infinite by default — so anything <c>StartAsync</c> awaits without a
/// deadline of its own can hang BTCPay's startup permanently, with no exception, no log line, and therefore no
/// auto-disable of the plugin. That is not hypothetical here: PR #6 shipped exactly that failure through a
/// different route, and the SDK connect was for a long time the remaining un-guarded instance of the same
/// class — bounded only by <c>Connect</c> happening to do no network I/O, which is an SDK property and not a
/// guarantee this plugin holds.
/// </para>
/// <para>
/// So these tests run <c>StartAsync</c> on its own background thread and <c>Join</c> a timeout, the same shape
/// <c>SparkPluginStartupTests</c> uses and for the same reason: a regression must fail the test rather than
/// hang the test run forever. The hanging connect is modelled as a task that never completes and ignores
/// cancellation, because no SDK call can be cancelled — see <see cref="FakeSparkSdkClientFactory"/>.
/// </para>
/// </remarks>
public class SparkServiceStartupTests
{
    /// <summary>
    /// How long startup may take before it is treated as hung.
    /// </summary>
    /// <remarks>
    /// Two orders of magnitude above the harness's 250 ms connect deadline, so a loaded machine cannot make
    /// this flaky. The failure it guards against never completes at any timeout.
    /// </remarks>
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(30);

    private const string HangingStore = "store-that-hangs";
    private const string HealthyStore = "store-that-works";

    [Fact]
    public void A_store_whose_SDK_connect_never_returns_does_not_hold_up_the_host()
    {
        using var h = SparkServiceHarness.Create();
        h.SeedStore(HangingStore, SparkServiceHarness.MnemonicFor(1));
        h.Sdk.HangFor.Add(HangingStore);

        var elapsed = StartWithinTimeout(h);

        // The whole point. Without the deadline this never returns and BTCPay never starts.
        Assert.True(
            elapsed < StartTimeout,
            $"StartAsync took {elapsed.TotalSeconds:0.0}s against a 250 ms connect deadline");

        // And the operator is told which store, and what it costs them — the silent version of this failure is
        // what made PR #6 so expensive to diagnose.
        Assert.Contains(HangingStore, h.Log.AllText);
        Assert.Contains("exceeded", h.Log.AllText);
    }

    [Fact]
    public async Task A_hanging_store_leaves_its_own_wallet_not_running_and_says_so()
    {
        using var h = SparkServiceHarness.Create();
        h.SeedStore(HangingStore, SparkServiceHarness.MnemonicFor(1));
        h.Sdk.HangFor.Add(HangingStore);

        StartWithinTimeout(h);

        // Not running, so the connection-string handler reports a transient failure rather than handing a
        // checkout a client that does not exist.
        Assert.Null(await h.Service.GetClient(HangingStore));
        Assert.Empty(await h.Service.GetRunningStoreIds());

        // The settings are still cached, though: the store is configured, it just has no wallet up.
        Assert.NotNull(await h.Service.Get(HangingStore));
    }

    [Fact]
    public async Task One_stores_hanging_connect_does_not_stop_another_stores_wallet_from_starting()
    {
        using var h = SparkServiceHarness.Create();
        h.SeedStore(HangingStore, SparkServiceHarness.MnemonicFor(1));
        h.SeedStore(HealthyStore, SparkServiceHarness.MnemonicFor(2));
        h.Sdk.HangFor.Add(HangingStore);

        StartWithinTimeout(h);

        Assert.NotNull(await h.Service.GetClient(HealthyStore));
        Assert.Null(await h.Service.GetClient(HangingStore));
        Assert.Equal([HealthyStore], await h.Service.GetRunningStoreIds());
    }

    [Fact]
    public async Task A_store_whose_connect_throws_does_not_stop_another_stores_wallet_from_starting()
    {
        using var h = SparkServiceHarness.Create();
        h.SeedStore("store-that-throws", SparkServiceHarness.MnemonicFor(3));
        h.SeedStore(HealthyStore, SparkServiceHarness.MnemonicFor(2));
        h.Sdk.FailFor["store-that-throws"] = new InvalidOperationException("the SDK refused this seed");

        StartWithinTimeout(h);

        Assert.NotNull(await h.Service.GetClient(HealthyStore));
        Assert.Null(await h.Service.GetClient("store-that-throws"));
    }

    /// <summary>
    /// A connect that arrives after its deadline must be shut down, not left running unreferenced.
    /// </summary>
    /// <remarks>
    /// The deadline abandons the wait, never the call, so the SDK will eventually hand back a live wallet on
    /// the store's own SQLite file — one that still serves the network and still mints invoices. Dropping the
    /// reference would leave the store with an unreachable live wallet <em>and</em> let a later configure put a
    /// second instance on the same one, which is the corruption the whole single-instance guard exists to
    /// prevent.
    /// </remarks>
    [Fact]
    public async Task A_connect_that_finishes_after_its_deadline_is_disconnected_and_disposed()
    {
        using var h = SparkServiceHarness.Create();
        h.SeedStore(HangingStore, SparkServiceHarness.MnemonicFor(1));
        h.Sdk.HangFor.Add(HangingStore);

        StartWithinTimeout(h);

        var late = h.Sdk.Release(HangingStore);

        await WaitUntil(() => late.Disposed, "the late wallet to be shut down");
        Assert.True(late.Disconnected, "Disconnect must precede Dispose: Dispose alone leaves it minting invoices");

        // And it is emphatically not adopted as the store's instance.
        Assert.Null(await h.Service.GetClient(HangingStore));
    }

    /// <summary>
    /// A connect that hangs forever must not lock its store out of its own wallet until the process restarts.
    /// </summary>
    /// <remarks>
    /// Audit finding InfraAndLogging F2. The abandoned connect kept the store's storage lock while it awaited a
    /// task that no SDK call can cancel, and that lock is a <c>FileShare.None</c> handle enforced between
    /// descriptors in the same process. So the store's own next attempt failed with "Another process is already
    /// using this store's Spark wallet storage" — accusing a second BTCPay of a hold this process was doing to
    /// itself — and reconfiguring could never clear it. Only a restart could.
    /// </remarks>
    [Fact]
    public async Task A_permanently_hung_connect_releases_the_storage_lock_so_the_store_can_be_reconfigured()
    {
        using var h = SparkServiceHarness.Create(
            abandonedConnectGrace: TimeSpan.FromMilliseconds(250));
        h.SeedStore(HangingStore, SparkServiceHarness.MnemonicFor(1));
        h.Sdk.HangFor.Add(HangingStore);

        StartWithinTimeout(h);

        // The connect is never released, modelling a stall that outlives any useful wait.
        await WaitUntil(
            () => h.Log.AllText.Contains("Releasing the storage lock"),
            "the abandoned connect's grace period to expire");

        // The decisive half: the store can now be started again rather than being told another process holds it.
        h.Sdk.HangFor.Remove(HangingStore);
        var settings = (await h.Service.Get(HangingStore))!;
        var applied = await h.Service.Set(HangingStore, settings);

        // Before the fix this came back not-running, with a reason blaming another process for a lock this
        // process was holding against itself.
        Assert.True(applied.WalletRunning, $"the store should be running again, got: {applied.Reason}");
        Assert.NotNull(await h.Service.GetClient(HangingStore));
    }

    /// <summary>
    /// Nothing is left buffering events for a wallet no consumer was ever started for.
    /// </summary>
    [Fact]
    public void An_abandoned_connect_has_its_event_channel_completed()
    {
        using var h = SparkServiceHarness.Create();
        h.SeedStore(HangingStore, SparkServiceHarness.MnemonicFor(1));
        h.Sdk.HangFor.Add(HangingStore);

        StartWithinTimeout(h);

        var writer = h.Sdk.EventWriters[HangingStore];
        Assert.False(
            writer.TryWrite(new Sdk.SparkEventEnvelope(HangingStore, Sdk.SparkEventKind.Synced, null)),
            "a completed writer refuses, which is what makes the listener report the loss rather than hide it");
    }

    /// <summary>
    /// Runs <c>StartAsync</c> the way the host does, on its own thread, and fails rather than hanging.
    /// </summary>
    /// <remarks>
    /// A dedicated thread rather than <c>Task.Run</c> plus <c>Wait(timeout)</c>, matching
    /// <c>SparkPluginStartupTests</c>: the failure being guarded against parks a thread indefinitely, and
    /// parking a pool thread would degrade the rest of the run instead of failing this test. The thread is a
    /// background thread so a genuinely hung startup cannot keep the process alive.
    /// </remarks>
    private static TimeSpan StartWithinTimeout(SparkServiceHarness h)
    {
        Exception? failure = null;
        var stopwatch = Stopwatch.StartNew();

        var thread = new Thread(() =>
        {
            try
            {
                h.Service.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        })
        {
            IsBackground = true,
            Name = "spark-service-start"
        };

        thread.Start();

        if (!thread.Join(StartTimeout))
        {
            Assert.Fail(
                $"SparkService.StartAsync did not complete within {StartTimeout.TotalSeconds:0}s. "
                + "HostOptions.StartupTimeout is infinite by default, so on a real server BTCPay would never "
                + "finish starting — with no exception, and therefore no log line and no auto-disable of the "
                + "plugin. Every SDK call this method awaits needs a deadline of its own.");
        }

        stopwatch.Stop();

        if (failure is not null)
            throw new InvalidOperationException("SparkService.StartAsync threw; startup must not fail.", failure);

        return stopwatch.Elapsed;
    }

    private static async Task WaitUntil(Func<bool> condition, string what)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition())
                return;
            await Task.Delay(10);
        }

        Assert.Fail($"Timed out waiting for {what}.");
    }
}
