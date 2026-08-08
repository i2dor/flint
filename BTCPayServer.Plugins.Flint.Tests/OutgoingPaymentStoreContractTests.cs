using BTCPayServer.Plugins.Flint.Data;
using BTCPayServer.Plugins.Flint.Tests.Fakes;
using BTCPayServer.Plugins.Flint.Tests.Postgres;
using Xunit;

namespace BTCPayServer.Plugins.Flint.Tests;

/// <summary>
/// The <see cref="IOutgoingPaymentStore"/> contract, asserted against every implementation.
/// </summary>
/// <remarks>
/// Written as a contract for the same reason as the invoice store's: the client tests run against the in-memory
/// implementation, and they only mean something if the real one agrees. This one has already earned its keep —
/// the in-memory store keyed on <c>(StoreId, PaymentHash)</c> while the EF store keyed on the payment hash
/// alone, so the two diverged precisely where a cross-store collision happens, and no test could see it.
/// </remarks>
public abstract class OutgoingPaymentStoreContractTests
{
    private const string StoreId = "store-1";
    private const string OtherStoreId = "store-2";
    private const string Bolt11 = "lnbcrt-one";

    protected abstract Task<IOutgoingPaymentStore> CreateStoreAsync();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_first_attempt_is_recorded_with_no_report()
    {
        var store = await CreateStoreAsync();
        var now = DateTimeOffset.UtcNow;

        var record = await store.RegisterAttemptAsync(
            StoreId, PaymentFixture.PaymentHash, "key-1", Bolt11, now, Ct);

        Assert.Equal(PaymentFixture.PaymentHash, record.PaymentHash);
        Assert.Equal(StoreId, record.StoreId);
        Assert.Equal("key-1", record.IdempotencyKey);
        Assert.Equal(Bolt11, record.Bolt11);
        Assert.Equal(1, record.AttemptCount);
        Assert.Null(record.ReportedAt);
    }

    [Fact]
    public async Task Repeat_attempts_accumulate()
    {
        var store = await CreateStoreAsync();
        var now = DateTimeOffset.UtcNow;

        await store.RegisterAttemptAsync(StoreId, PaymentFixture.PaymentHash, "key-1", Bolt11, now, Ct);
        await store.RegisterAttemptAsync(StoreId, PaymentFixture.PaymentHash, "key-1", Bolt11, now, Ct);
        var third = await store.RegisterAttemptAsync(
            StoreId, PaymentFixture.PaymentHash, "key-1", Bolt11, now, Ct);

        Assert.Equal(3, third.AttemptCount);
    }

    [Fact]
    public async Task Exactly_one_caller_may_report_a_payment()
    {
        var store = await CreateStoreAsync();
        var now = DateTimeOffset.UtcNow;
        await store.RegisterAttemptAsync(StoreId, PaymentFixture.PaymentHash, "key-1", Bolt11, now, Ct);

        Assert.True(await store.TryMarkReportedAsync(StoreId, PaymentFixture.PaymentHash, now, Ct));
        Assert.False(await store.TryMarkReportedAsync(StoreId, PaymentFixture.PaymentHash, now, Ct));
    }

    [Fact]
    public async Task A_report_is_visible_to_the_next_attempt()
    {
        // This is what the client reads to decide whether a second claim on the same invoice must be refused, so
        // it has to survive the round trip.
        var store = await CreateStoreAsync();
        var now = DateTimeOffset.UtcNow;
        await store.RegisterAttemptAsync(StoreId, PaymentFixture.PaymentHash, "key-1", Bolt11, now, Ct);
        await store.TryMarkReportedAsync(StoreId, PaymentFixture.PaymentHash, now, Ct);

        var next = await store.RegisterAttemptAsync(
            StoreId, PaymentFixture.PaymentHash, "key-1", Bolt11, now, Ct);

        Assert.NotNull(next.ReportedAt);
        Assert.Equal(2, next.AttemptCount);
    }

    [Fact]
    public async Task Reporting_an_unknown_payment_reports_false()
    {
        var store = await CreateStoreAsync();

        Assert.False(await store.TryMarkReportedAsync(
            StoreId, PaymentFixture.PaymentHash, DateTimeOffset.UtcNow, Ct));
    }

    [Fact]
    public async Task Two_stores_can_each_be_asked_to_pay_the_same_invoice()
    {
        // The defect this pins. Two stores on one server can each hold a payout for the same BOLT11 — a shared
        // supplier invoice, say. With a payment-hash-only primary key the second store's insert collided with the
        // first store's row, the store-scoped read that followed found nothing, and the synthesized fallback
        // reported ReportedAt = null forever. A legitimate crash-retry of a payment that had already been sent
        // would then be reported to BTCPay as a failure — and, worse, a genuine second claim would never be
        // refused.
        var store = await CreateStoreAsync();
        var now = DateTimeOffset.UtcNow;

        var mine = await store.RegisterAttemptAsync(
            StoreId, PaymentFixture.PaymentHash, "key-1", Bolt11, now, Ct);
        var theirs = await store.RegisterAttemptAsync(
            OtherStoreId, PaymentFixture.PaymentHash, "key-1", Bolt11, now, Ct);

        Assert.Equal(StoreId, mine.StoreId);
        Assert.Equal(OtherStoreId, theirs.StoreId);
        // Each store's row is its own, with its own attempt count.
        Assert.Equal(1, mine.AttemptCount);
        Assert.Equal(1, theirs.AttemptCount);
    }

    [Fact]
    public async Task One_stores_report_does_not_consume_another_stores_right_to_report()
    {
        var store = await CreateStoreAsync();
        var now = DateTimeOffset.UtcNow;
        await store.RegisterAttemptAsync(StoreId, PaymentFixture.PaymentHash, "key-1", Bolt11, now, Ct);
        await store.RegisterAttemptAsync(OtherStoreId, PaymentFixture.PaymentHash, "key-1", Bolt11, now, Ct);

        Assert.True(await store.TryMarkReportedAsync(StoreId, PaymentFixture.PaymentHash, now, Ct));

        // The other store has not paid anything yet, so it must still be allowed to.
        Assert.True(await store.TryMarkReportedAsync(OtherStoreId, PaymentFixture.PaymentHash, now, Ct));

        var theirs = await store.RegisterAttemptAsync(
            OtherStoreId, PaymentFixture.PaymentHash, "key-1", Bolt11, now, Ct);
        Assert.NotNull(theirs.ReportedAt);
    }

    [Fact]
    public async Task A_stores_attempts_are_counted_independently()
    {
        var store = await CreateStoreAsync();
        var now = DateTimeOffset.UtcNow;

        await store.RegisterAttemptAsync(StoreId, PaymentFixture.PaymentHash, "key-1", Bolt11, now, Ct);
        await store.RegisterAttemptAsync(StoreId, PaymentFixture.PaymentHash, "key-1", Bolt11, now, Ct);
        var theirs = await store.RegisterAttemptAsync(
            OtherStoreId, PaymentFixture.PaymentHash, "key-1", Bolt11, now, Ct);

        Assert.Equal(1, theirs.AttemptCount);
    }
}

/// <summary>The contract against the in-memory implementation used by the client tests.</summary>
public class InMemoryOutgoingPaymentStoreTests : OutgoingPaymentStoreContractTests
{
    protected override Task<IOutgoingPaymentStore> CreateStoreAsync() =>
        Task.FromResult<IOutgoingPaymentStore>(new InMemoryOutgoingPaymentStore());
}

/// <summary>The same contract against the production EF store and a real Postgres database.</summary>
[Trait("Category", "Postgres")]
[Collection(PostgresTestDatabase.CollectionName)]
public class PostgresOutgoingPaymentStoreTests : OutgoingPaymentStoreContractTests
{
    private readonly PostgresTestDatabase _database;

    public PostgresOutgoingPaymentStoreTests(PostgresTestDatabase database) => _database = database;

    protected override async Task<IOutgoingPaymentStore> CreateStoreAsync() =>
        new EfOutgoingPaymentStore(await _database.CreateFactoryAsync());
}
