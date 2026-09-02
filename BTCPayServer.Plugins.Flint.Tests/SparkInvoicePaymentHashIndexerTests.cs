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
/// The decisions in <see cref="SparkInvoicePaymentHashIndexer"/>: which prompt-mint events become the plugin's
/// own payment-hash → invoice association, and whether the server has any Flint store for the association to
/// matter to.
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
/// <para>
/// Every recording fact runs against the real <see cref="SparkService"/> with one store provisioned, through
/// <see cref="SparkServiceHarness"/> — the gate the writes pass is that service's live settings cache, and a
/// fake of it would make the recording facts assert only that the fake says yes. The no-store skip and the
/// turn-on-when-provisioned facts run against the same harness with nothing seeded, because the gate is read
/// per event and must open the moment the first store exists, without a restart.
/// </para>
/// </remarks>
public class SparkInvoicePaymentHashIndexerTests
{
    private const string InvoiceId = "btcpay-invoice-1";
    private const string StoreId = "flint-store-1";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// The harness's service, started so its startup gate is open — seeded with one store when
    /// <paramref name="provisioned"/>, which is what the recording facts need the gate to pass for.
    /// </summary>
    private static async Task<SparkServiceHarness> StartedSpark(bool provisioned)
    {
        var h = SparkServiceHarness.Create();
        if (provisioned)
            h.SeedStore(StoreId, SparkServiceHarness.MnemonicFor(1));
        await h.Service.StartAsync(Ct);
        return h;
    }

    private static SparkInvoicePaymentHashIndexer Create(
        SparkService sparkService,
        out InMemoryInvoicePaymentHashIndex index)
    {
        var logs = new Logs();
        logs.Configure(NullLoggerFactory.Instance);
        index = new InMemoryInvoicePaymentHashIndex();
        return new SparkInvoicePaymentHashIndexer(
            new BTCPayServer.EventAggregator(logs),
            index,
            sparkService,
            NullLogger<SparkInvoicePaymentHashIndexer>.Instance);
    }

    private static PaymentMethodId Pmi(string cryptoCode, PaymentType type) => type.GetPaymentMethodId(cryptoCode);

    private static uint256 Hash() => uint256.Parse(PaymentFixture.PaymentHash);

    [Fact]
    public async Task An_LNURL_prompt_mint_records_the_association()
    {
        // The case this exists for: an LNURL prompt, which BTCPay's own index only covers while LUD-21 is on.
        using var spark = await StartedSpark(provisioned: true);
        var indexer = Create(spark.Service, out var index);
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
        using var spark = await StartedSpark(provisioned: true);
        var indexer = Create(spark.Service, out var index);
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
        // — and a same-named hash could in principle be minted on two networks without meaning the same
        // thing. Provisioned, so the skip below is provably the payment-method filter rather than the store
        // gate.
        using var spark = await StartedSpark(provisioned: true);
        var indexer = Create(spark.Service, out var index);
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
        using var spark = await StartedSpark(provisioned: true);
        var indexer = Create(spark.Service, out var index);
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
        using var spark = await StartedSpark(provisioned: true);
        var indexer = Create(spark.Service, out var index);
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
        using var spark = await StartedSpark(provisioned: true);
        var indexer = Create(spark.Service, out var index);
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
        using var spark = await StartedSpark(provisioned: true);
        var indexer = Create(spark.Service, out var index);
        await indexer.RecordAssociationAsync(
            new InvoiceNewPaymentDetailsEvent(
                InvoiceId,
                new LNURLPayPaymentMethodDetails { PaymentHash = uint256.Parse(PaymentFixture.PaymentHash.ToUpperInvariant()) },
                Pmi("BTC", PaymentTypes.LNURL)),
            Ct);

        var stored = Assert.Single(index.Entries.Values);
        Assert.Equal(PaymentFixture.PaymentHash, stored.PaymentHash);
    }

    [Fact]
    public async Task A_prompt_mint_records_nothing_while_no_store_has_flint()
    {
        // Core publishes the mint event for every store's prompt whether or not this plugin serves anyone,
        // and on a server with no Flint store the write is noise the plugin's table never gets to use:
        // core's own index covers the association for every store this plugin does not serve.
        using var spark = await StartedSpark(provisioned: false);
        Assert.False(await spark.Service.HasAnyStoreProvisioned());
        var indexer = Create(spark.Service, out var index);

        await indexer.RecordAssociationAsync(
            new InvoiceNewPaymentDetailsEvent(
                InvoiceId,
                new LNURLPayPaymentMethodDetails { PaymentHash = Hash() },
                Pmi("BTC", PaymentTypes.LNURL)),
            Ct);

        Assert.Empty(index.Entries);
    }

    [Fact]
    public async Task Provisioning_the_first_store_turns_recording_on_without_a_restart()
    {
        // The gate is re-read on every event rather than latched at startup, and this pins it: the same
        // indexer instance that skipped before the store existed records the very next prompt after the
        // settings cache gains its first entry. A cached flag with no invalidation hook would fail here.
        using var spark = await StartedSpark(provisioned: false);
        var indexer = Create(spark.Service, out var index);
        var firstPrompt = new InvoiceNewPaymentDetailsEvent(
            InvoiceId,
            new LNURLPayPaymentMethodDetails { PaymentHash = Hash() },
            Pmi("BTC", PaymentTypes.LNURL));

        await indexer.RecordAssociationAsync(firstPrompt, Ct);
        Assert.Empty(index.Entries);

        var applied = await spark.Service.Set(StoreId, new SparkSettings
        {
            ProtectedMnemonic = spark.Protector.Protect(SparkServiceHarness.MnemonicFor(2)),
            PaymentKey = SparkConnectionString.GeneratePaymentKey(),
            SeedSource = SeedSource.Generated
        });
        Assert.True(applied.WalletRunning);
        Assert.True(await spark.Service.HasAnyStoreProvisioned());

        await indexer.RecordAssociationAsync(firstPrompt, Ct);

        var entry = Assert.Single(index.Entries.Values);
        Assert.Equal(InvoiceId, entry.InvoiceId);
    }
}
