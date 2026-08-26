using BTCPayServer.Plugins.Flint.Services;

namespace BTCPayServer.Plugins.Flint.Tests.Fakes;

/// <summary>
/// An <see cref="IInvoiceCreditGateway"/> that models the two indexes the real one talks to.
/// </summary>
/// <remarks>
/// <para>
/// Not a stub that returns configured answers: the properties the credit path depends on are properties of
/// BTCPay's schema, and a fake that did not have them would make every assertion here hollow. What is modelled
/// is what the real gateway consults, in the order it consults it.
/// </para>
/// <para>
/// <b><c>AddressInvoices</c> is insert-only.</b> BTCPay writes the payment hash of every prompt it issues
/// into that table when the prompt is minted and never removes it, so superseding BOLT11 X with BOLT11 Y
/// leaves <em>both</em> hashes pointing at the same invoice. That is the entire basis for crediting a payment
/// to a replaced BOLT11, so <see cref="Mint"/> only ever adds.
/// </para>
/// <para>
/// <b>The plugin's own payment-hash index is written regardless of LUD-21.</b> The real gateway falls back to
/// <see cref="Services.IInvoicePaymentHashIndex"/> when core's table has no row, which happens for an LNURL
/// prompt minted while the merchant had LUD-21 disabled by hand (core indexes LNURL prompts only when LUD-21
/// is on). <see cref="Mint"/> therefore writes the plugin index <em>always</em> and core's
/// <c>AddressInvoices</c> only when told to, so a test can reproduce the LUD-21-off shape by minting with
/// <c>indexInCore: false</c>.
/// </para>
/// <para>
/// <b><c>Payments</c> has primary key <c>(Id, PaymentMethodId)</c>.</b> That collision — not any ordering or
/// lock — is what makes the credit exactly-once when BTCPay's own listener and this plugin both try. A second
/// insert for the same pair is refused with <see cref="SparkInvoiceCreditOutcome.AlreadyRecorded"/>, exactly
/// as core's <c>PaymentService.AddPayment</c> answers null on the <c>DbUpdateException</c>.
/// </para>
/// <para>
/// <b>An invoice has exactly one <em>current</em> payment prompt, and it carries the preimage.</b> The third
/// property, and the one that is easiest to get wrong in the opposite direction from the first: the hash index
/// keeps every BOLT11 an invoice ever offered, but the prompt holds only the latest, and the prompt is where
/// LUD-21 <c>verify</c> reads proof-of-payment from. So <see cref="Mint"/> <em>replaces</em> the prompt while
/// only adding to the index — that asymmetry is the superseding — and a credit fills the prompt's preimage only
/// when the prompt is still offering the BOLT11 that was paid.
/// </para>
/// </remarks>
public sealed class FakeInvoiceCreditGateway : IInvoiceCreditGateway
{
    /// <summary>The payment method ids BTCPay indexes a Lightning payment hash under.</summary>
    public const string LightningPaymentMethodId = "BTC-LN";

    /// <inheritdoc cref="LightningPaymentMethodId" />
    public const string LnurlPaymentMethodId = "BTC-LNURL";

    private readonly Dictionary<string, Row> _addressInvoices = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Row> _pluginIndex = new(StringComparer.Ordinal);
    private readonly HashSet<(string Id, string PaymentMethodId)> _payments = [];
    private readonly Dictionary<string, Prompt> _prompts = new(StringComparer.Ordinal);

    private sealed record Row(string InvoiceId, string StoreId, string PaymentMethodId);

    /// <summary>An invoice's current payment prompt: the BOLT11 it offers now, and its preimage once known.</summary>
    private sealed class Prompt
    {
        public required string PaymentHash { get; init; }
        public string? Preimage { get; set; }
    }

    /// <summary>Every accepted insert, in order. One entry per payment actually credited.</summary>
    public List<SparkInvoiceCreditRequest> Credits { get; } = [];

    /// <summary>Every attempt, accepted or not — so a test can tell "refused" from "never tried".</summary>
    public List<SparkInvoiceCreditRequest> Attempts { get; } = [];

    /// <summary>How many times a payment hash has been looked up.</summary>
    public int Lookups { get; private set; }

    /// <summary>
    /// Payment hashes whose invoice reports no usable payment prompt, to exercise the not-retryable path.
    /// </summary>
    public HashSet<string> PromptMissingFor { get; } = [];

    /// <summary>Thrown by both methods when set, to exercise the failure path.</summary>
    public Exception? FailWith { get; set; }

    /// <summary>
    /// Records that this BOLT11's payment hash was minted for an invoice, as happens when a prompt is minted.
    /// </summary>
    /// <param name="indexInCore">
    /// Whether core also recorded the hash in its <c>AddressInvoices</c> table. False models an LNURL prompt
    /// minted while LUD-21 was disabled by hand: core does not index it, but the plugin's own index — written
    /// from the mint event regardless of LUD-21 — does.
    /// </param>
    public FakeInvoiceCreditGateway Mint(
        string paymentHash,
        string invoiceId,
        string storeId,
        string paymentMethodId = LightningPaymentMethodId,
        bool indexInCore = true)
    {
        // Insert-only, like the real tables: superseding a BOLT11 does not remove the old hash's row.
        if (indexInCore)
            _addressInvoices.TryAdd(paymentHash, new Row(invoiceId, storeId, paymentMethodId));
        _pluginIndex.TryAdd(paymentHash, new Row(invoiceId, storeId, paymentMethodId));
        // The prompt, by contrast, is replaced — this invoice now offers this BOLT11 and no longer the previous
        // one. Minting X then Y is exactly the supersession the credit path exists for.
        _prompts[invoiceId] = new Prompt { PaymentHash = paymentHash };
        return this;
    }

    /// <summary>
    /// The preimage on an invoice's current payment prompt, or null when nothing has filled it in.
    /// </summary>
    /// <remarks>
    /// This is the field LUD-21 <c>verify</c> serves proof-of-payment from — not the payment row's copy — which
    /// is why it is asserted on separately. BTCPay's own listener fills it only when its own insert wins, so
    /// whenever this plugin wins the race instead, filling it is this plugin's job.
    /// </remarks>
    public string? PromptPreimageFor(string invoiceId) =>
        _prompts.GetValueOrDefault(invoiceId)?.Preimage;

    /// <summary>The BOLT11 payment hash an invoice's prompt currently offers.</summary>
    public string? PromptPaymentHashFor(string invoiceId) =>
        _prompts.GetValueOrDefault(invoiceId)?.PaymentHash;

    /// <summary>
    /// Records a payment against an invoice the way BTCPay's own Lightning listener would, so a test can set
    /// up "core got there first".
    /// </summary>
    public FakeInvoiceCreditGateway CreditedByBTCPay(
        string paymentHash,
        string paymentMethodId = LightningPaymentMethodId)
    {
        _payments.Add((paymentHash, paymentMethodId));
        return this;
    }

    /// <summary>Payments recorded against one BTCPay invoice, whoever recorded them.</summary>
    public IReadOnlyList<SparkInvoiceCreditRequest> CreditsFor(string invoiceId) =>
        Credits.Where(c => c.InvoiceId == invoiceId).ToList();

    public Task<SparkInvoiceCreditMatch?> FindByPaymentHashAsync(
        string paymentHash,
        CancellationToken cancellationToken = default)
    {
        if (FailWith is not null)
            throw FailWith;

        Lookups++;
        // The real gateway consults core's table first and the plugin's own index only when that has no row —
        // the LUD-21-off LNURL case modelled by Mint(..., indexInCore: false). Either index can resolve the
        // hash; core's index just takes precedence, exactly as in the production gateway.
        var row = _addressInvoices.GetValueOrDefault(paymentHash)
                  ?? _pluginIndex.GetValueOrDefault(paymentHash);
        if (row is null)
        {
            return Task.FromResult<SparkInvoiceCreditMatch?>(null);
        }
        return Task.FromResult<SparkInvoiceCreditMatch?>(new SparkInvoiceCreditMatch(
            row.InvoiceId,
            row.StoreId,
            row.PaymentMethodId,
            _payments.Contains((paymentHash, row.PaymentMethodId))));
    }

    public Task<SparkInvoiceCreditOutcome> AddSettledPaymentAsync(
        SparkInvoiceCreditRequest request,
        CancellationToken cancellationToken = default)
    {
        if (FailWith is not null)
            throw FailWith;

        Attempts.Add(request);

        // "Which invoice was this minted for" is the question, and either index can answer it — the same
        // existence test as the real gateway's re-read of the invoice by id.
        if (!_addressInvoices.ContainsKey(request.PaymentHash)
            && !_pluginIndex.ContainsKey(request.PaymentHash))
        {
            return Task.FromResult(SparkInvoiceCreditOutcome.InvoiceGone);
        }

        if (PromptMissingFor.Contains(request.PaymentHash))
            return Task.FromResult(SparkInvoiceCreditOutcome.PromptMissing);

        // The primary key decides, exactly as it does in Postgres.
        if (!_payments.Add((request.PaymentHash, request.PaymentMethodId)))
            return Task.FromResult(SparkInvoiceCreditOutcome.AlreadyRecorded);

        Credits.Add(request);

        // And the winner of that insert backfills the prompt's preimage — but only if the prompt is still
        // offering the BOLT11 that was paid. Crediting a superseded X must not stamp X's preimage onto the
        // prompt now offering Y, which would have LUD-21 verify hand a payer a proof for an invoice they did not
        // pay.
        if (request.Preimage is not null
            && _prompts.TryGetValue(request.InvoiceId, out var prompt)
            && prompt.Preimage is null
            && string.Equals(prompt.PaymentHash, request.PaymentHash, StringComparison.Ordinal))
        {
            prompt.Preimage = request.Preimage;
        }

        return Task.FromResult(SparkInvoiceCreditOutcome.CreditedNow);
    }
}
