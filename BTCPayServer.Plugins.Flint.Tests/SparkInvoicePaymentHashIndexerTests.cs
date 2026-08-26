using BTCPayServer.Events;
using BTCPayServer.Logging;
using BTCPayServer.Payments;
using BTCPayServer.Payments.Lightning;
using BTCPayServer.Plugins.Flint.Data;
using BTCPayServer.Plugins.Flint.Services;
using BTCPayServer.Plugins.Flint.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using NBitcoin;
using Xunit;

namespace BTCPayServer.Plugins.Flint.Tests;

/// <summary>
/// The decision in <see cref="SparkInvoicePaymentHashIndexer"/>: which prompt-mint events become the plugin's
/// own payment-hash → invoice association.
/// </summary>
/// <remarks>
/// <para>
/// This class is what makes a late payment to a superseded LNURL BOLT11 creditable when the merchant disabled
/// LUD-21 by hand — the case BTCPay's own <c>AddressInvoices</c> index does not cover, because core writes an
/// LNURL prompt's hash there only while LUD-21 is on, while <c>InvoiceNewPaymentDetailsEvent</c> fires on
/// every mint. Driving <see cref="SparkInvoicePaymentHashIndexer.RecordAssociationAsync"/> directly pins the
/// decision; the wiring from the event aggregator to it is covered by
/// <see cref="SparkPluginStartupTests"/>, which resolves the hosted service from BTCPay's own container.
/// </para>
/// </remarks>
public class SparkInvoicePaymentHashIndexerTests
{
    private const string InvoiceId = "btcpay-invoice-1";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static SparkInvoicePaymentHashIndexer Create(out InMemoryInvoicePaymentHashIndex index)
    {
        var logs = new Logs();
        logs.Configure(NullLoggerFactory.Instance);
        index = new InMemoryInvoicePaymentHashIndex();
        return new SparkInvoicePaymentHashIndexer(
            new BTCPayServer.EventAggregator(logs),
            index,
            NullLogger<SparkInvoicePaymentHashIndexer>.Instance);
    }

    private static PaymentMethodId Pmi(string cryptoCode, PaymentType type) => type.GetPaymentMethodId(cryptoCode);

    private static uint256 Hash() => uint256.Parse(PaymentFixture.PaymentHash);

    [Fact]
    public async Task An_LNURL_prompt_mint_records_the_association()
    {
        // The case this exists for: an LNURL prompt, which BTCPay's own index only covers while LUD-21 is on.
        var indexer = Create(out var index);
        await indexer.RecordAssociationAsync(
            new InvoiceNewPaymentDetailsEvent(
                InvoiceId,
                new LNURLPayPaymentMethodDetails { PaymentHash = Hash() },
                Pmi("BTC", PaymentTypes.LNURL)),
            Ct);

        var entry = await index.FindByPaymentHashAsync(PaymentFixture.PaymentHash, Ct);
        Assert.NotNull(entry);
        Assert.Equal(InvoiceId, entry.InvoiceId);
        Assert.Equal("BTC-LNURL", entry.PaymentMethodId);
        Assert.NotEqual(default, entry.FirstSeenAt);
    }

    [Fact]
    public async Task A_plain_lightning_prompt_mint_records_the_association()
    {
        // A plain checkout prompt: core always indexes this hash itself, so this row is redundant — but the
        // event carries it too, and recording both rails keeps the index uniform.
        var indexer = Create(out var index);
        await indexer.RecordAssociationAsync(
            new InvoiceNewPaymentDetailsEvent(
                InvoiceId,
                new LigthningPaymentPromptDetails { PaymentHash = Hash() },
                Pmi("BTC", PaymentTypes.LN)),
            Ct);

        var entry = await index.FindByPaymentHashAsync(PaymentFixture.PaymentHash, Ct);
        Assert.NotNull(entry);
        Assert.Equal("BTC-LN", entry.PaymentMethodId);
    }

    [Fact]
    public async Task A_prompt_for_another_crypto_is_not_recorded()
    {
        // The plugin credits only Bitcoin Lightning payments, so another crypto's prompts are not its business
        // — and a same-named hash could in principle be minted on two networks without meaning the same thing.
        var indexer = Create(out var index);
        await indexer.RecordAssociationAsync(
            new InvoiceNewPaymentDetailsEvent(
                InvoiceId,
                new LigthningPaymentPromptDetails { PaymentHash = Hash() },
                Pmi("LTC", PaymentTypes.LN)),
            Ct);

        Assert.Null(await index.FindByPaymentHashAsync(PaymentFixture.PaymentHash, Ct));
    }

    [Fact]
    public async Task A_prompt_without_a_payment_hash_yet_is_not_recorded()
    {
        // The LNURL request event precedes the mint that gives a prompt its hash; there is nothing to index
        // until the mint event follows.
        var indexer = Create(out var index);
        await indexer.RecordAssociationAsync(
            new InvoiceNewPaymentDetailsEvent(
                InvoiceId,
                new LNURLPayPaymentMethodDetails { PaymentHash = null! },
                Pmi("BTC", PaymentTypes.LNURL)),
            Ct);

        Assert.Empty(index.Entries);
    }

    [Fact]
    public async Task A_prompt_of_an_unrelated_payment_type_is_not_recorded()
    {
        // The details of an on-chain prompt, say, derive from a different base and carry no payment hash.
        var indexer = Create(out var index);
        await indexer.RecordAssociationAsync(
            new InvoiceNewPaymentDetailsEvent(
                InvoiceId,
                new object(),
                Pmi("BTC", PaymentTypes.LNURL)),
            Ct);

        Assert.Empty(index.Entries);
    }

    [Fact]
    public async Task A_failed_index_write_is_swallowed_and_logged_not_thrown()
    {
        // A prompt association is not worth unwinding the event-aggregator loop over: for a store with LUD-21
        // on, core's own index covers the hash anyway, and the next mint of the same invoice re-fires the
        // event. The base class's loop logs unhandled exceptions and continues, but this method must not even
        // get to that — it is called from ProcessEvent deliberately.
        var indexer = Create(out var index);
        index.FailRecordWith = new InvalidOperationException("db down");

        await indexer.RecordAssociationAsync(
            new InvoiceNewPaymentDetailsEvent(
                InvoiceId,
                new LNURLPayPaymentMethodDetails { PaymentHash = Hash() },
                Pmi("BTC", PaymentTypes.LNURL)),
            Ct);

        Assert.Empty(index.Entries);
    }

    [Fact]
    public async Task The_stored_hash_is_lower_case()
    {
        // The primary key is case-sensitive, and the caller's event could carry the hash in either case.
        var indexer = Create(out var index);
        await indexer.RecordAssociationAsync(
            new InvoiceNewPaymentDetailsEvent(
                InvoiceId,
                new LNURLPayPaymentMethodDetails { PaymentHash = uint256.Parse(PaymentFixture.PaymentHash.ToUpperInvariant()) },
                Pmi("BTC", PaymentTypes.LNURL)),
            Ct);

        var stored = Assert.Single(index.Entries.Values);
        Assert.Equal(PaymentFixture.PaymentHash, stored.PaymentHash);
    }
}
