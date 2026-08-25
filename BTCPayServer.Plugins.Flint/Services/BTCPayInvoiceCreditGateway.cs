using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Data;
using BTCPayServer.Events;
using BTCPayServer.Lightning;
using BTCPayServer.Payments;
using BTCPayServer.Payments.Lightning;
using BTCPayServer.Services.Invoices;
using Microsoft.Extensions.Logging;
using NBitcoin;
using NBitcoin.Crypto;
using NBitcoin.DataEncoders;

namespace BTCPayServer.Plugins.Flint.Services;

/// <summary>
/// The real <see cref="IInvoiceCreditGateway"/>, over BTCPay's invoice repository and payment service.
/// </summary>
/// <remarks>
/// <para>
/// Does what core's own <c>LightningListener.AddPaymentCore</c> does, from outside the listener, and with the
/// same identifiers — which is the whole point. The payment id is the payment hash, exactly as core would
/// write it, so the two inserts collide on the <c>Payments</c> primary key <c>(Id, PaymentMethodId)</c> and
/// exactly one of them records the money. Core's <c>PaymentService.AddPayment</c> catches the resulting
/// <c>DbUpdateException</c> and answers null, which this class reports as
/// <see cref="SparkInvoiceCreditOutcome.AlreadyRecorded"/>. Crediting a merchant twice for one payment is
/// therefore not prevented by ordering or by a lock, but by the database.
/// </para>
/// <para>
/// <b>The prompt preimage is backfilled, but only onto the prompt it belongs to.</b> Core's listener also
/// writes the preimage into the invoice's <em>current</em> payment prompt, and that write is not decoration:
/// LUD-21 <c>verify</c> serves proof-of-payment out of the prompt, not out of the payment row
/// (<c>UILNURLController</c>), and this plugin forces LUD-21 on for every store it provisions. Skipping it
/// would therefore have cost every ordinary Flint checkout its proof-of-payment — because core's listener
/// performs that backfill only when <em>its own</em> insert wins, and this class usually wins the race. So the
/// backfill is replicated here, guarded on the current prompt's payment hash being the hash of the payment
/// being credited. The guard is what makes both cases right: an ordinary settlement is crediting the current
/// prompt and the preimage belongs there, while a superseded BOLT11 X is being credited against a prompt that
/// now offers replacement Y, whose preimage this is not, and the guard leaves it alone.
/// </para>
/// <para>
/// A failed backfill is logged and swallowed. The payment row is already in by then, which is the part that
/// credits the merchant; unwinding it to protect a proof-of-payment field would trade money for metadata.
/// </para>
/// <para>
/// <b>The credited payment's blob describes the current prompt, not the BOLT11 that was paid.</b> Core's
/// <c>PaymentData.Set</c> fills the payment's destination, network fee and divisibility from the invoice's
/// current prompt, so for a superseded BOLT11 X the row's destination reads as replacement Y's BOLT11. The
/// paid BOLT11 is still recorded — it is passed as the payment's search term — and the payment hash, which is
/// the row's id, is X's. Cosmetic, inherent to the core helper this deliberately reuses rather than
/// reimplements, and not worth diverging from core to prettify.
/// </para>
/// <para><b>Why <see cref="PaymentService"/> arrives as a factory.</b></para>
/// <para>
/// Belt-and-braces, and consistency with the deferral this plugin already establishes elsewhere, rather than a
/// live hazard. Its constructor takes <c>PaymentMethodHandlerDictionary</c>, whose construction enumerates
/// <c>IEnumerable&lt;IPaymentMethodHandler&gt;</c>, which builds core's <c>LightningLikePaymentHandler</c>,
/// which takes <c>LightningClientFactoryService</c>, which takes
/// <c>IEnumerable&lt;ILightningConnectionStringHandler&gt;</c> — a list this plugin contributes to. That was
/// one leg of the cycle whose eager resolution deadlocked BTCPay's startup once already (see
/// <see cref="BTCPayStoreLightningConfigStore"/>), but the cycle is now broken at its other end:
/// <c>SparkConnectionStringHandler</c> takes a <c>Func&lt;ISparkClientResolver&gt;</c>, so injecting
/// <c>PaymentService</c> directly here would not in fact re-form it. It is deferred anyway because every
/// plugin service that touches core's payment graph defers, and a uniform rule is easier to keep true than a
/// per-service judgement about which edges are currently harmless.
/// <see cref="InvoiceRepository"/> is injected directly because it takes only a context factory and the event
/// aggregator, and so is not on that graph at all.
/// </para>
/// </remarks>
public sealed class BTCPayInvoiceCreditGateway : IInvoiceCreditGateway
{
    /// <summary>Spark is Bitcoin-only, and so is every payment this plugin can credit.</summary>
    internal const string CryptoCode = "BTC";

    /// <summary>
    /// Where a payment hash may be indexed, in the order it is looked for.
    /// </summary>
    /// <remarks>
    /// <c>BTC-LN</c> first because a plain Lightning checkout always indexes the hash there. <c>BTC-LNURL</c>
    /// second because LNURL prompts index it only when LUD-21 is enabled — which this plugin forces on when it
    /// provisions a store (<see cref="BTCPayStoreLightningConfigStore.SetAsync"/>), so for a Flint store both
    /// rails are covered. A merchant who turned LUD-21 off by hand loses the LNURL half of this, and the
    /// credit for such a payment is reported as unresolvable rather than guessed at.
    /// </remarks>
    internal static readonly PaymentMethodId[] CreditablePaymentMethods =
    [
        PaymentTypes.LN.GetPaymentMethodId(CryptoCode),
        PaymentTypes.LNURL.GetPaymentMethodId(CryptoCode)
    ];

    private readonly InvoiceRepository _invoiceRepository;
    private readonly Func<PaymentService> _paymentService;
    private readonly Func<PaymentMethodHandlerDictionary> _handlers;
    private readonly EventAggregator _eventAggregator;
    private readonly ILogger<BTCPayInvoiceCreditGateway> _logger;

    /// <param name="paymentService">
    /// Resolved on first use rather than injected, and that is load-bearing: see the remarks on this class.
    /// </param>
    /// <param name="handlers">Deferred for the same reason, and shared with the rest of the plugin.</param>
    public BTCPayInvoiceCreditGateway(
        InvoiceRepository invoiceRepository,
        Func<PaymentService> paymentService,
        Func<PaymentMethodHandlerDictionary> handlers,
        EventAggregator eventAggregator,
        ILogger<BTCPayInvoiceCreditGateway> logger)
    {
        _invoiceRepository = invoiceRepository;
        _paymentService = paymentService;
        _handlers = handlers;
        _eventAggregator = eventAggregator;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<SparkInvoiceCreditMatch?> FindByPaymentHashAsync(
        string paymentHash,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(paymentHash);

        foreach (var paymentMethodId in CreditablePaymentMethods)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // BTCPay's AddressInvoices table, which is insert-only: the row written when the prompt was
            // issued survives the prompt being superseded, and is what makes a payment to a replaced BOLT11
            // attributable at all.
            var invoice = await _invoiceRepository
                .GetInvoiceFromAddress(paymentMethodId, paymentHash)
                .ConfigureAwait(false);
            if (invoice is null)
                continue;

            // "Is the primary key already taken", not "is the invoice accounted for". A payment row with this
            // id under this payment method — whatever its status — is a row our insert would collide with, and
            // that is exactly the question the caller needs answered.
            var alreadyHasPayment = invoice
                .GetPayments(false)
                .Any(p => p.Id == paymentHash && p.PaymentMethodId == paymentMethodId);

            return new SparkInvoiceCreditMatch(
                invoice.Id, invoice.StoreId, paymentMethodId.ToString(), alreadyHasPayment);
        }

        return null;
    }

    /// <inheritdoc />
    public async Task<SparkInvoiceCreditOutcome> AddSettledPaymentAsync(
        SparkInvoiceCreditRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var paymentMethodId = PaymentMethodId.Parse(request.PaymentMethodId);

        // Re-read rather than trusting the entity the lookup returned: the credit may be attempted a whole
        // reconciliation pass after the match was found, and AddPayment needs the invoice's current blob.
        var invoice = await _invoiceRepository.GetInvoice(request.InvoiceId).ConfigureAwait(false);
        if (invoice is null)
            return SparkInvoiceCreditOutcome.InvoiceGone;

        // Both of the next two checks exist to disambiguate AddPayment's null, which it returns for a missing
        // invoice, a missing prompt, a missing handler *and* a duplicate key alike. Without them a payment
        // that was never recorded would be reported as already recorded, and the credit would be marked done.
        if (!_handlers().TryGetValue(paymentMethodId, out var handler))
        {
            _logger.LogWarning(
                "BTCPay has no {PaymentMethodId} payment handler, so the settled payment {PaymentHash} cannot "
                + "be recorded on invoice {InvoiceId}. The money is in the Spark wallet and this plugin's own "
                + "records show it; the BTCPay invoice will not",
                paymentMethodId, request.PaymentHash, request.InvoiceId);
            return SparkInvoiceCreditOutcome.PromptMissing;
        }

        if (invoice.GetPaymentPrompt(paymentMethodId) is not { } prompt)
            return SparkInvoiceCreditOutcome.PromptMissing;

        if (invoice.GetPayments(false).Any(p => p.Id == request.PaymentHash
                                                && p.PaymentMethodId == paymentMethodId))
        {
            return SparkInvoiceCreditOutcome.AlreadyRecorded;
        }

        var paymentHash = uint256.Parse(request.PaymentHash);
        var preimage = ValidPreimageOrNull(request.Preimage, paymentHash, request.InvoiceId);
        var paymentData = new PaymentData
        {
            // The payment hash, which is the id core's own listener would use. That collision is what makes
            // the credit exactly-once.
            Id = request.PaymentHash,
            Created = request.PaidAt,
            Status = PaymentStatus.Settled,
            Currency = CryptoCode,
            InvoiceDataId = request.InvoiceId,
            Amount = ToBtc(request.AmountReceivedMsat)
        }.Set(invoice, handler, new LightningLikePaymentData
        {
            PaymentHash = paymentHash,
            Preimage = preimage
        });

        var payment = await _paymentService().AddPayment(paymentData, [request.Bolt11]).ConfigureAwait(false);
        if (payment is null)
        {
            // Everything else AddPayment answers null for was ruled out above, so what is left is the primary
            // key: core's listener, or a concurrent pass, recorded this payment between those checks and this
            // insert. Which is the outcome this design wants — one of us wins, and neither double-credits.
            return SparkInvoiceCreditOutcome.AlreadyRecorded;
        }

        // Our insert won, so this is also the caller that owes the prompt its preimage: core's listener performs
        // that backfill only on the branch where its own insert won, which this one just took from it.
        await BackfillPromptPreimageAsync(handler, prompt, paymentHash, preimage, request.InvoiceId)
            .ConfigureAwait(false);

        // Re-read so the event carries the invoice including the payment just added, exactly as core's
        // listener does — subscribers compute the invoice's new state from it.
        var credited = await _invoiceRepository.GetInvoice(request.InvoiceId).ConfigureAwait(false);
        if (credited is not null)
        {
            _eventAggregator.Publish(
                new InvoiceEvent(credited, InvoiceEvent.ReceivedPayment) { Payment = payment });
        }

        _logger.LogInformation(
            "Recorded the settled Lightning payment {PaymentHash} on BTCPay invoice {InvoiceId} "
            + "({PaymentMethodId}), which its Lightning listener was no longer watching for",
            request.PaymentHash, request.InvoiceId, paymentMethodId);

        return SparkInvoiceCreditOutcome.CreditedNow;
    }

    /// <summary>
    /// Writes the preimage onto the payment prompt, if and only if the prompt is offering the BOLT11 that was
    /// paid. Never throws.
    /// </summary>
    /// <remarks>
    /// See the class remarks for why this is here at all and why it is guarded. Never throws because the payment
    /// row is already committed by the time it runs: the merchant is credited either way, and an exception
    /// escaping would turn a completed credit into a logged failure and a retry that can only conclude
    /// "already recorded".
    /// </remarks>
    private async Task BackfillPromptPreimageAsync(
        IPaymentMethodHandler handler,
        PaymentPrompt prompt,
        uint256 paymentHash,
        uint256? preimage,
        string invoiceId)
    {
        if (preimage is null)
            return;

        try
        {
            // Both BTC-LN and BTC-LNURL prompt details derive from this type — core's LNURL details class
            // extends it — and re-serialisation goes through the handler's own serialiser on the runtime type,
            // so an LNURL prompt keeps its extra fields.
            var details = (LigthningPaymentPromptDetails)handler.ParsePaymentPromptDetails(prompt.Details);
            if (!ShouldBackfillPromptPreimage(details.PaymentHash, details.Preimage, paymentHash))
                return;

            details.Preimage = preimage;
            await _invoiceRepository.UpdatePaymentDetails(invoiceId, handler, details).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Recorded the settled Lightning payment {PaymentHash} on BTCPay invoice {InvoiceId} but could "
                + "not write its preimage onto the payment prompt. The payment stands; LNURL proof-of-payment "
                + "(LUD-21 verify) for this invoice will report no preimage",
                paymentHash, invoiceId);
        }
    }

    /// <summary>
    /// Whether the invoice's current prompt is the one this payment's preimage belongs on.
    /// </summary>
    /// <remarks>
    /// Two independent conditions, and each rules out a different wrong write. The hash comparison is the
    /// superseded case: crediting BOLT11 X against a prompt that now offers replacement Y must not stamp X's
    /// preimage as Y's, which would make LUD-21 <c>verify</c> hand a payer a proof for an invoice they did not
    /// pay. The null check is core's own condition, and it means the same thing here — a prompt that already
    /// carries a preimage was filled in by whoever got there first, and their value is the one that matches the
    /// prompt.
    /// </remarks>
    internal static bool ShouldBackfillPromptPreimage(
        uint256? promptPaymentHash,
        uint256? promptPreimage,
        uint256 paymentHash) =>
        promptPreimage is null && promptPaymentHash == paymentHash;

    /// <summary>The amount as BTCPay records it on a payment: millisatoshi in, BTC out.</summary>
    /// <remarks>
    /// Through <see cref="LightMoney"/> rather than by dividing, so the conversion is core's own and cannot
    /// drift from it — and so the units are named in the code rather than in a comment next to a literal.
    /// </remarks>
    internal static decimal ToBtc(long amountReceivedMsat) =>
        LightMoney.MilliSatoshis(amountReceivedMsat).ToDecimal(LightMoneyUnit.BTC);

    /// <summary>
    /// The preimage as BTCPay stores it, or null when it cannot be verified against the payment hash.
    /// </summary>
    /// <remarks>
    /// The logging wrapper around <see cref="ValidatePreimage"/>. Split so the validation itself — which is
    /// where the byte-order convention and the hash check live — is testable without a logger or a container.
    /// </remarks>
    private uint256? ValidPreimageOrNull(string? preimage, uint256 paymentHash, string invoiceId)
    {
        var validity = ValidatePreimage(preimage, paymentHash, out var stored);
        if (validity is PreimageValidity.Mismatched)
        {
            _logger.LogWarning(
                "The Spark service reported a preimage for {PaymentHash} that does not hash to it; BTCPay "
                + "invoice {InvoiceId} is credited without one",
                paymentHash, invoiceId);
        }

        return stored;
    }

    /// <summary>What a reported preimage turned out to be.</summary>
    internal enum PreimageValidity
    {
        /// <summary>None was reported. The service provider's HTLC details make it optional.</summary>
        Absent,

        /// <summary>Not 32 bytes of hex. Nothing to check against the hash.</summary>
        Malformed,

        /// <summary>Well-formed, but it does not hash to the payment hash. Worth an operator line.</summary>
        Mismatched,

        /// <summary>Verified against the payment hash, and returned in the form BTCPay stores.</summary>
        Valid
    }

    /// <summary>
    /// Verifies a reported preimage against its payment hash and converts it to the form BTCPay stores.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The same validation core performs (<c>LightningListener.GetValidPreimage</c>), replicated rather than
    /// skipped: a preimage is a proof, and an unverifiable one stored as though it were verified would let a
    /// future dispute be settled with a value nothing checked. Dropping it costs only that proof — the payment
    /// itself is recorded either way.
    /// </para>
    /// <para>
    /// <b>Two byte orders are in play and they are not interchangeable.</b> The hash is computed over the
    /// preimage's wire bytes and compared against <c>paymentHash.ToBytes(false)</c>, which is <c>uint256</c>'s
    /// big-endian rendering — the order the hex in a BOLT11 is written in. What is <em>stored</em> is a
    /// <c>uint256</c> built from those bytes reversed, because that is core's convention for the field; without
    /// the reversal the recorded preimage reads back inverted and would fail anyone's verification. Both halves
    /// are pinned by tests against a known preimage/hash pair rather than trusted to review.
    /// </para>
    /// </remarks>
    internal static PreimageValidity ValidatePreimage(
        string? preimage,
        uint256 paymentHash,
        out uint256? stored)
    {
        stored = null;

        if (string.IsNullOrEmpty(preimage))
            return PreimageValidity.Absent;
        if (preimage.Length != 64 || !HexEncoder.IsWellFormed(preimage))
            return PreimageValidity.Malformed;

        var candidate = Encoders.Hex.DecodeData(preimage);
        if (!Hashes.SHA256(candidate).AsSpan().SequenceEqual(paymentHash.ToBytes(false)))
            return PreimageValidity.Mismatched;

        Array.Reverse(candidate);
        stored = new uint256(candidate);
        return PreimageValidity.Valid;
    }
}
