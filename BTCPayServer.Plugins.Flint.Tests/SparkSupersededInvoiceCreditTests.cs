using System.Numerics;
using Breez.Sdk.Spark;
using BTCPayServer.Lightning;
using BTCPayServer.Plugins.Flint.Data;
using BTCPayServer.Plugins.Flint.Sdk;
using BTCPayServer.Plugins.Flint.Tests.Fakes;
using NBitcoin;
using Xunit;
using SdkPaymentStatus = Breez.Sdk.Spark.PaymentStatus;

namespace BTCPayServer.Plugins.Flint.Tests;

/// <summary>
/// The audit's superseded-invoice scenario, end to end: X → Y → restart → pay X.
/// </summary>
/// <remarks>
/// <para>
/// <b>The defect this pins.</b> BTCPay's Lightning listener matches a settlement notification by id against a
/// set it builds from each invoice's <em>current</em> payment prompt. Replace BOLT11 X with BOLT11 Y — which
/// BTCPay does whenever an LNURL invoice is re-quoted — and X leaves that set; after a restart it is never
/// re-added, because the set is rebuilt from the prompts that exist now. X is still payable, because Spark has
/// no way to withdraw an invoice from the service provider. So a payment to X after that restart used to land
/// in the merchant's wallet, settle in this plugin's records, and leave the BTCPay invoice unpaid: money
/// received against an invoice that says it was not.
/// </para>
/// <para>
/// <b>Why it is tested here rather than as a unit.</b> Every ingredient is a seam of its own — the record
/// store, the broadcaster, the creditor, the gateway — and each of them can be individually correct while the
/// scenario still fails, because what fails is the <em>composition</em>: which state survives a restart and
/// which does not. So this drives the real <see cref="Services.SparkService"/> through
/// <see cref="SparkServiceHarness"/>, mints and supersedes through the real
/// <see cref="SparkLightningClient"/>, and restarts by actually discarding the service and its in-memory
/// broadcaster while keeping the database and BTCPay's payment-hash index — the split a real restart makes.
/// </para>
/// </remarks>
public class SparkSupersededInvoiceCreditTests
{
    private const string StoreId = "superseded-store";
    private const string InvoiceId = "btcpay-invoice-1";

    /// <summary>The superseded BOLT11 and its replacement, both minted for the same BTCPay invoice.</summary>
    private const string Bolt11X = "lnbcrt-x";

    private const string Bolt11Y = "lnbcrt-y";

    private static readonly string HashX = PaymentFixture.PaymentHash;
    private static readonly string HashY = PaymentFixture.OtherPaymentHash;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // -------------------------------------------------------------------------------------------------------
    // Fixtures
    // -------------------------------------------------------------------------------------------------------

    /// <summary>
    /// A completed inbound Lightning payment, in the SDK's own shape.
    /// </summary>
    /// <remarks>
    /// Timestamped now rather than at a literal instant, because the settlement time is what the credit's retry
    /// horizon is measured from: a hard-coded timestamp would put these settlements outside the horizon as soon
    /// as the calendar moved past it, and the tests would start failing for a reason that has nothing to do
    /// with the behaviour they name.
    /// </remarks>
    private static Payment Receive(string id, string hash, long amountSats = 100) =>
        new(
            id: id,
            paymentType: PaymentType.Receive,
            status: SdkPaymentStatus.Completed,
            amount: new BigInteger(amountSats),
            fees: BigInteger.Zero,
            timestamp: (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            method: PaymentMethod.Lightning,
            details: new PaymentDetails.Lightning(
                description: "order 42",
                invoice: "lnbcrt-one",
                destinationPubkey: "02fe4b",
                htlcDetails: new SparkHtlcDetails(hash, PaymentFixture.Preimage, 0, SparkHtlcStatus.PreimageShared),
                lnurlPayInfo: null,
                lnurlWithdrawInfo: null,
                lnurlReceiveMetadata: null,
                conversionInfo: null),
            conversionDetails: null!);

    private static void Pay(SparkServiceHarness h, string hash, string sdkPaymentId) =>
        Assert.True(
            h.Sdk.EventWriters[StoreId].TryWrite(
                new SparkEventEnvelope(StoreId, SparkEventKind.PaymentSucceeded, Receive(sdkPaymentId, hash))),
            "the event channel refused the envelope");

    private static async Task WaitFor(Func<bool> condition, string because)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
                return;
            await Task.Delay(10, Ct);
        }

        Assert.Fail(because);
    }

    /// <summary>A started service with one configured store, ready to mint.</summary>
    private static async Task<(SparkServiceHarness Harness, string PaymentKey)> StartedAsync()
    {
        var h = SparkServiceHarness.Create();
        var paymentKey = SparkConnectionString.GeneratePaymentKey();
        h.SeedStore(StoreId, SparkServiceHarness.MnemonicFor(1), paymentKey);
        var now = DateTimeOffset.UtcNow;
        h.Bolt11.Register(Bolt11X, new Bolt11Info(HashX, now.AddHours(1), 100_000));
        h.Bolt11.Register(Bolt11Y, new Bolt11Info(HashY, now.AddHours(1), 100_000));

        await h.Service.StartAsync(CancellationToken.None);
        return (h, paymentKey);
    }

    private static ILightningClient ClientFor(SparkServiceHarness h, string paymentKey)
    {
        var resolution = h.Service.Resolve(StoreId, paymentKey, NBitcoin.Network.RegTest);
        Assert.Null(resolution.Error);
        Assert.NotNull(resolution.Client);
        return resolution.Client;
    }

    /// <summary>Mints one invoice through the real client and indexes it the way BTCPay does.</summary>
    private static async Task<LightningInvoice> MintAsync(
        SparkServiceHarness h,
        ILightningClient client,
        string bolt11)
    {
        h.Sdk.Clients[StoreId].NextPaymentRequest = bolt11;
        var invoice = await client.CreateInvoice(
            new CreateInvoiceParams(LightMoney.Satoshis(100), "order 42", TimeSpan.FromHours(1)), Ct);

        // What core's LightningLikePaymentHandler.ConfigurePrompt does immediately after this returns: it
        // writes the payment hash into AddressInvoices against the invoice it is prompting for. Insert-only,
        // so superseding this BOLT11 later does not remove the row.
        h.Credits.Mint(invoice.Id, InvoiceId, StoreId);
        return invoice;
    }

    // -------------------------------------------------------------------------------------------------------
    // The audit's scenario
    // -------------------------------------------------------------------------------------------------------

    [Fact(Timeout = 120_000)]
    public async Task A_superseded_invoice_paid_after_a_restart_credits_its_BTCPay_invoice_exactly_once()
    {
        var (first, paymentKey) = await StartedAsync();
        SparkServiceHarness? h = null;
        try
        {
            var client = ClientFor(first, paymentKey);

            // BTCPay quotes the invoice, then re-quotes it: X is cancelled and replaced by Y, both minted for
            // the same BTCPay invoice.
            var x = await MintAsync(first, client, Bolt11X);
            await client.CancelInvoice(x.Id, Ct);
            var y = await MintAsync(first, client, Bolt11Y);
            Assert.Equal(HashX, x.Id);
            Assert.Equal(HashY, y.Id);

            // The restart. The service, its SDK connections and the settlement broadcaster are discarded; the
            // database and BTCPay's payment-hash index survive, as they do on a real server. From here on
            // BTCPay is watching only Y — nothing in this process knows X exists except the record store.
            h = first.Restart();
            await h.Service.StartAsync(CancellationToken.None);

            // And X is paid.
            Pay(h, HashX, "recv-x");

            await WaitFor(
                () => h.Invoices.Records[HashX] is { Status: InvoiceRecordStatus.Paid, CreditedAt: not null },
                "the payment to the superseded invoice never reached its BTCPay invoice");

            // Exactly one payment, on the right invoice, for the amount that actually arrived.
            var credit = Assert.Single(h.Credits.CreditsFor(InvoiceId));
            Assert.Equal(HashX, credit.PaymentHash);
            Assert.Equal(100_000, credit.AmountReceivedMsat);
            Assert.Equal(PaymentFixture.Preimage, credit.Preimage);

            // And the replacement's payment prompt is untouched. X's preimage is on X's payment, not stamped
            // onto the prompt now offering Y — which would have LUD-21 verify hand a payer a proof of payment
            // for an invoice they never paid.
            Assert.Equal(HashY, h.Credits.PromptPaymentHashFor(InvoiceId));
            Assert.Null(h.Credits.PromptPreimageFor(InvoiceId));

            // A duplicate event — the SDK does emit them, 57 ms apart on two threads in one observed case —
            // must not credit it again. Nor must a reconciliation pass, which walks the same rows.
            var seen = h.Sdk.Clients[StoreId].GetPaymentCalls.Count;
            Pay(h, HashX, "recv-x");
            // Waited on an observable effect of the duplicate having been consumed, so this asserts that the
            // duplicate was processed and credited nothing — not merely that it had not arrived yet.
            await WaitFor(
                () => h.Sdk.Clients[StoreId].GetPaymentCalls.Count > seen,
                "the duplicate settlement event was never consumed");
            await h.Service.ReconcileAllStoresAsync(Ct);
            await h.Service.ReconcileAllStoresAsync(Ct);

            Assert.Single(h.Credits.CreditsFor(InvoiceId));

            // The replacement still works, under its own hash, on the same BTCPay invoice: nothing about the
            // routing above interferes with the ordinary path.
            Pay(h, HashY, "recv-y");
            await WaitFor(
                () => h.Invoices.Records[HashY] is { Status: InvoiceRecordStatus.Paid, CreditedAt: not null },
                "the replacement invoice never settled");

            Assert.Equal(
                [HashX, HashY], h.Credits.CreditsFor(InvoiceId).Select(c => c.PaymentHash).ToArray());

            // Y *is* the current prompt, so crediting it does fill the prompt's preimage — the ordinary
            // proof-of-payment path, which the superseded case above must not disturb and does not.
            Assert.Equal(PaymentFixture.Preimage, h.Credits.PromptPreimageFor(InvoiceId));
        }
        finally
        {
            (h ?? first).Dispose();
        }
    }

    [Fact(Timeout = 120_000)]
    public async Task A_settlement_recorded_while_the_process_was_down_is_credited_by_the_next_pass()
    {
        // The crash window. The settlement's compare-and-set committed, and the process died before BTCPay was
        // told — so the row is paid, uncredited, and nothing is ever going to notify anyone about it again. The
        // startup reconciliation pass is what has to notice.
        var (first, _) = await StartedAsync();
        SparkServiceHarness? h = null;
        try
        {
            first.Invoices.Seed(new InvoiceRecord
            {
                PaymentHash = HashX,
                StoreId = StoreId,
                Bolt11 = Bolt11X,
                AmountMsat = 100_000,
                AmountReceivedMsat = 100_000,
                SdkPaymentId = "recv-x",
                Preimage = PaymentFixture.Preimage,
                CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-30),
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-20),
                SettledAt = DateTimeOffset.UtcNow.AddMinutes(-25),
                Status = InvoiceRecordStatus.Paid
            });
            first.Credits.Mint(HashX, InvoiceId, StoreId);

            h = first.Restart();
            await h.Service.StartAsync(CancellationToken.None);

            await WaitFor(
                () => h.Invoices.Records[HashX].CreditedAt is not null,
                "the settlement recorded before the restart was never credited");

            var credit = Assert.Single(h.Credits.CreditsFor(InvoiceId));
            Assert.Equal(HashX, credit.PaymentHash);

            // Note that this row is past its expiry: the settlement walk would not look at it any more, and
            // BTCPay's listener never would. Only the credit walk reaches it.
            await h.Service.ReconcileAllStoresAsync(Ct);
            Assert.Single(h.Credits.CreditsFor(InvoiceId));
        }
        finally
        {
            (h ?? first).Dispose();
        }
    }

    [Fact(Timeout = 120_000)]
    public async Task A_settlement_BTCPay_already_credited_is_not_credited_twice_after_a_restart()
    {
        // The common case, which must stay cheap and silent: BTCPay's own listener credited the payment before
        // the restart. The pass afterwards has to recognise that and stop asking, without inserting anything.
        var (first, paymentKey) = await StartedAsync();
        SparkServiceHarness? h = null;
        try
        {
            var client = ClientFor(first, paymentKey);
            var x = await MintAsync(first, client, Bolt11X);

            Pay(first, HashX, "recv-x");
            await WaitFor(
                () => first.Invoices.Records[HashX] is
                    { Status: InvoiceRecordStatus.Paid, CreditedAt: not null },
                "the invoice never settled and credited");

            // Rewritten as though core's listener had won the race and this plugin had not yet stamped the row
            // — the exact state a crash between the two leaves behind.
            first.Credits.CreditedByBTCPay(x.Id);
            first.Invoices.Records[HashX].CreditedAt = null;
            first.Credits.Credits.Clear();

            h = first.Restart();
            await h.Service.StartAsync(CancellationToken.None);

            await WaitFor(
                () => h.Invoices.Records[HashX].CreditedAt is not null,
                "the pass never recognised that BTCPay already held the payment");
            // Nothing inserted: the payment was already on the invoice, and finding that out is the whole job.
            Assert.Empty(h.Credits.Credits);
        }
        finally
        {
            (h ?? first).Dispose();
        }
    }
}
