using BTCPayServer.Plugins.Flint.Data;
using BTCPayServer.Plugins.Flint.Services;
using BTCPayServer.Plugins.Flint.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BTCPayServer.Plugins.Flint.Tests;

/// <summary>
/// The bound and the pass of <see cref="SparkPaymentHashRetentionTask"/>.
/// </summary>
/// <remarks>
/// The data layer is deliberately not exercised here: <c>EfInvoicePaymentHashIndex.PruneBeforeAsync</c> is a
/// set <c>ExecuteDeleteAsync</c> that only runs against Postgres, and its predicate is pinned by
/// <see cref="InvoicePaymentHashIndexContractTests"/> — whose in-memory half runs in this suite and whose
/// Postgres half runs when <c>SPARK_POSTGRES_TESTS</c> is set. What is left to pin in-process is the task's
/// own two decisions: where the cutoff comes from, and that a pass acts on it.
/// </remarks>
public class SparkPaymentHashRetentionTaskTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string StaleHash =
        "c1d2e3f4051627384950a1b2c3d4e5f60718293a4b5c6d7e8f901234567800cc";
    private const string KeptHash =
        "d2e3f4051627384950a1b2c3d4e5f60718293a4b5c6d7e8f901234567800ccdd";

    private static InvoicePaymentHash Row(string hash, DateTimeOffset firstSeenAt) => new()
    {
        PaymentHash = hash,
        InvoiceId = "btcpay-invoice-1",
        PaymentMethodId = "BTC-LNURL",
        FirstSeenAt = firstSeenAt
    };

    [Fact]
    public void The_retention_window_is_the_credit_walks_own_listing_floor()
    {
        // Not a copied constant: retention prunes exactly what the walk can no longer list, so widening
        // SparkInvoiceCreditor's horizons moves this boundary with it, and a future edit that "simplifies"
        // the cutoff to a bare 14 days — or, worse, to the shorter CreditRetryHorizon, which would delete
        // rows the walk still lists for another week — fails here.
        var now = new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

        Assert.Equal(SparkInvoiceCreditor.ListableFrom(now), SparkPaymentHashRetentionTask.CutoffFor(now));
        Assert.Equal(now - TimeSpan.FromDays(14), SparkPaymentHashRetentionTask.CutoffFor(now));
    }

    [Fact]
    public async Task A_pass_removes_only_associations_older_than_the_window()
    {
        var index = new InMemoryInvoicePaymentHashIndex();
        var cutoff = SparkPaymentHashRetentionTask.CutoffFor(DateTimeOffset.UtcNow);
        // One minute either side of the boundary: a pass that prunes by settlement-adjacent clocks rather
        // than first-seen time, or whose comparison is off by an equality flip at the cutoff, is caught by
        // which side of this pair dies.
        index.Seed(Row(StaleHash, cutoff - TimeSpan.FromMinutes(1)));
        index.Seed(Row(KeptHash, cutoff + TimeSpan.FromMinutes(1)));

        await new SparkPaymentHashRetentionTask(
            index,
            NullLogger<SparkPaymentHashRetentionTask>.Instance)
            .Do(Ct);

        Assert.Null(await index.FindByPaymentHashAsync(StaleHash, Ct));
        Assert.NotNull(await index.FindByPaymentHashAsync(KeptHash, Ct));
        Assert.Single(index.Entries);
    }

    [Fact]
    public async Task A_pass_on_an_empty_or_fresh_table_is_a_no_op()
    {
        // The ordinary case on a healthy server, on nearly every pass: nothing past the window exists, and
        // the pass must leave the recent rows alone rather than sweep the table.
        var index = new InMemoryInvoicePaymentHashIndex();
        index.Seed(Row(KeptHash, DateTimeOffset.UtcNow));

        await new SparkPaymentHashRetentionTask(
            index,
            NullLogger<SparkPaymentHashRetentionTask>.Instance)
            .Do(Ct);

        Assert.NotNull(await index.FindByPaymentHashAsync(KeptHash, Ct));
    }
}
