using BTCPayServer.Plugins.Flint.Data;
using BTCPayServer.Plugins.Flint.Services;
using BTCPayServer.Plugins.Flint.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Network = NBitcoin.Network;
using Xunit;

namespace BTCPayServer.Plugins.Flint.Tests;

/// <summary>
/// The sweep engine stands down while a store has a unilateral exit in progress.
/// </summary>
/// <remarks>
/// <para>
/// <b>The hazard is the plugin working against itself.</b> An exit is pinned to named leaves and funded against
/// them; a cooperative sweep spends those same leaves. Before this pause, the automatic sweep kept running beside
/// a quoted or built exit, so the next build found the leaves gone and the operator's funding sat committed to an
/// exit that could no longer be built or finished. The sweep is the one leaf-spender the plugin fully controls, so
/// it is the one that stops; the exit page says which others it cannot stop.
/// </para>
/// </remarks>
public class SparkSweepEngineExitPauseTests
{
    private const string StoreId = "store-1";

    private static readonly DateTimeOffset Origin = new(2026, 8, 4, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(UnilateralExitStatus.AwaitingFunding, SweepTrigger.Automatic)]
    [InlineData(UnilateralExitStatus.Built, SweepTrigger.Automatic)]
    [InlineData(UnilateralExitStatus.Built, SweepTrigger.Manual)]
    public async Task An_exit_in_progress_pauses_sweeping_with_a_recorded_reason(
        UnilateralExitStatus status, SweepTrigger trigger)
    {
        var h = Create();
        await h.Exits.CreateAsync(Exit(status), Ct);

        var result = await h.Engine.RunAsync(StoreId, trigger, Ct);

        Assert.Equal(SweepOutcomeKind.Refused, result.Kind);
        Assert.Equal(SparkSweepEngine.UnilateralExitInProgress, result.Reason);
        Assert.Empty(h.Sdk.OnchainSendCalls);
        // Visible where every other refusal is: in the sweep history, with its own code.
        var recorded = Assert.Single(h.Sweeps.Records.Values);
        Assert.Equal(SweepRecordStatus.Refused, recorded.Status);
        Assert.Equal(SweepRefusalCode.ExitInProgress, recorded.RefusalCode);

        // The confirmation page says the same before anyone presses the button.
        var preview = await h.Engine.PreviewAsync(StoreId, Ct);
        Assert.Equal(SparkSweepEngine.UnilateralExitInProgress, preview.RefusalReason);
    }

    [Fact]
    public async Task Repeated_automatic_passes_fold_onto_one_refusal()
    {
        var h = Create();
        await h.Exits.CreateAsync(Exit(UnilateralExitStatus.Built), Ct);

        await h.Engine.RunAsync(StoreId, SweepTrigger.Automatic, Ct);
        await h.Engine.RunAsync(StoreId, SweepTrigger.Automatic, Ct);

        // An exit runs for days; a row every scheduler pass would bury everything else in the history.
        var recorded = Assert.Single(h.Sweeps.Records.Values);
        Assert.Equal(2, recorded.AttemptCount);
    }

    [Fact]
    public async Task A_finished_exit_lets_sweeping_resume()
    {
        var h = Create();
        var exit = Exit(UnilateralExitStatus.Built);
        await h.Exits.CreateAsync(exit, Ct);
        exit.Status = UnilateralExitStatus.Abandoned;
        Assert.True(await h.Exits.UpdateAsync(exit, UnilateralExitStatus.Built, Ct));

        var result = await h.Engine.RunAsync(StoreId, SweepTrigger.Automatic, Ct);

        Assert.NotEqual(SparkSweepEngine.UnilateralExitInProgress, result.Reason);
        Assert.NotEmpty(h.Sdk.OnchainSendCalls);
    }

    [Fact]
    public async Task An_exit_store_that_cannot_be_read_is_not_taken_as_no_exit()
    {
        var h = Create();
        h.Exits.FailReadsWith = new InvalidOperationException("the database is not answering");

        var result = await h.Engine.RunAsync(StoreId, SweepTrigger.Automatic, Ct);

        Assert.Equal(SweepOutcomeKind.Refused, result.Kind);
        Assert.Equal(SparkSweepEngine.UnilateralExitUnknown, result.Reason);
        Assert.Empty(h.Sdk.OnchainSendCalls);
    }

    private sealed record Harness(
        SparkSweepEngine Engine,
        FakeSparkSdkClient Sdk,
        InMemorySweepRecordStore Sweeps,
        InMemoryUnilateralExitRecordStore Exits);

    private static Harness Create()
    {
        var log = new WriteLog();
        var sdk = new FakeSparkSdkClient(log) { BalanceSats = 500_000 };
        var sweeps = new InMemorySweepRecordStore(log);
        var exits = new InMemoryUnilateralExitRecordStore();
        var settings = new FakeSparkStoreSettingsStore();
        settings.Settings[StoreId] = new SparkSettings
        {
            ProtectedMnemonic = "protected",
            PaymentKey = "key",
            Sweep = new SweepSettings { Enabled = true }
        };

        var runtime = new FakeSparkStoreRuntime();
        runtime.Clients[StoreId] = sdk;

        var engine = new SparkSweepEngine(
            settings,
            runtime,
            sweeps,
            new SweepDestinationResolver(
                new FakeSweepAddressSource(), Network.RegTest, NullLogger<SweepDestinationResolver>.Instance),
            new CrossChainRouteResolver(NullLogger<CrossChainRouteResolver>.Instance),
            new FakeCrossChainValueOracle(),
            new FakeSweepTransactionLabeler(),
            new StubTimeProvider(Origin),
            NullLogger<SparkSweepEngine>.Instance,
            exits);

        return new Harness(engine, sdk, sweeps, exits);
    }

    private static UnilateralExitRecord Exit(UnilateralExitStatus status) => new()
    {
        Id = "exit-1",
        StoreId = StoreId,
        Status = status,
        CreatedUtc = Origin,
        UpdatedUtc = Origin,
        DestinationAddress = "bcrt1qtxwcjjvf4ny9wsw9emgnpazey2vde3xhnyqpw0",
        FeeRateSatPerVbyte = 10,
        LeafIdsJson = """["leaf-a"]""",
        FundingAddress = "bcrt1qluxw544vs8huwqyxvwqx4x75x5v7mgfkamt2pd"
    };
}
