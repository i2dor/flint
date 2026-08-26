using BTCPayServer.Plugins.Flint.Data;
using BTCPayServer.Plugins.Flint.Tests.Fakes;
using BTCPayServer.Plugins.Flint.Tests.Postgres;
using Xunit;

namespace BTCPayServer.Plugins.Flint.Tests;

/// <summary>
/// The <see cref="IInvoicePaymentHashIndex"/> contract, asserted against every implementation.
/// </summary>
/// <remarks>
/// <para>
/// Written as a contract rather than as tests of one class for the same reason the invoice-record store is:
/// the in-memory implementation is what the rest of the suite runs against, and those tests only mean
/// something if the production EF store behaves identically — notably that a recorded association is
/// write-once (first writer wins) and that lookups are case-insensitive on the hex.
/// </para>
/// <para>
/// The Postgres subclass is skipped unless <c>SPARK_POSTGRES_TESTS</c> holds a connection string.
/// </para>
/// </remarks>
public abstract class InvoicePaymentHashIndexContractTests
{
    private const string InvoiceId = "btcpay-invoice-1";
    private const string PaymentMethodId = "BTC-LNURL";

    protected abstract Task<IInvoicePaymentHashIndex> CreateIndexAsync();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static InvoicePaymentHash Entry(string? hash = null, string? invoiceId = null) => new()
    {
        PaymentHash = hash ?? PaymentFixture.PaymentHash,
        InvoiceId = invoiceId ?? InvoiceId,
        PaymentMethodId = PaymentMethodId
    };

    [Fact]
    public async Task A_recorded_hash_is_found_with_the_invoice_it_was_minted_for()
    {
        var index = await CreateIndexAsync();
        await index.RecordAsync(Entry(), Ct);

        var found = await index.FindByPaymentHashAsync(PaymentFixture.PaymentHash, Ct);
        Assert.NotNull(found);
        Assert.Equal(InvoiceId, found.InvoiceId);
        Assert.Equal(PaymentMethodId, found.PaymentMethodId);
        Assert.NotEqual(default, found.FirstSeenAt);

        // A hash that was never minted for a BTCPay invoice is the "no association" answer.
        Assert.Null(await index.FindByPaymentHashAsync(PaymentFixture.OtherPaymentHash, Ct));
    }

    [Fact]
    public async Task A_hash_is_found_regardless_of_hex_case()
    {
        // BTCPay is not consistent about the case of a payment hash, and the association must not care.
        // (The contract pins the normalisation both implementations perform on the way in.)
        var index = await CreateIndexAsync();
        await index.RecordAsync(Entry(hash: PaymentFixture.PaymentHash.ToUpperInvariant()), Ct);

        Assert.NotNull(await index.FindByPaymentHashAsync(PaymentFixture.PaymentHash, Ct));
        Assert.NotNull(
            await index.FindByPaymentHashAsync(PaymentFixture.PaymentHash.ToUpperInvariant(), Ct));
    }

    [Fact]
    public async Task The_first_writer_of_a_hash_wins()
    {
        // A payment hash is unique to the BOLT11 minted for one invoice, so a second association for the
        // same hash cannot be legitimate — including one minted for the same invoice, which is what a plain
        // retry of a mint event looks like. The first row must survive, not be overwritten, mirroring core's
        // insert-only AddressInvoices.
        var index = await CreateIndexAsync();
        await index.RecordAsync(Entry(invoiceId: InvoiceId), Ct);
        var original = await index.FindByPaymentHashAsync(PaymentFixture.PaymentHash, Ct);
        Assert.NotNull(original);

        await index.RecordAsync(Entry(invoiceId: "a-different-invoice"), Ct);

        var after = await index.FindByPaymentHashAsync(PaymentFixture.PaymentHash, Ct);
        Assert.NotNull(after);
        Assert.Equal(InvoiceId, after.InvoiceId);
        Assert.Equal(original.FirstSeenAt, after.FirstSeenAt);
    }
}

/// <summary>The contract against the in-memory implementation used by the rest of the suite.</summary>
public class InMemoryInvoicePaymentHashIndexTests : InvoicePaymentHashIndexContractTests
{
    protected override Task<IInvoicePaymentHashIndex> CreateIndexAsync() =>
        Task.FromResult<IInvoicePaymentHashIndex>(new InMemoryInvoicePaymentHashIndex());
}

/// <summary>
/// The same contract against the production EF store and a real Postgres database.
/// </summary>
[Trait("Category", "Postgres")]
[Collection(PostgresTestDatabase.CollectionName)]
public class PostgresInvoicePaymentHashIndexTests : InvoicePaymentHashIndexContractTests
{
    private readonly PostgresTestDatabase _database;

    public PostgresInvoicePaymentHashIndexTests(PostgresTestDatabase database) => _database = database;

    protected override async Task<IInvoicePaymentHashIndex> CreateIndexAsync() =>
        new EfInvoicePaymentHashIndex(await _database.CreateFactoryAsync());
}
