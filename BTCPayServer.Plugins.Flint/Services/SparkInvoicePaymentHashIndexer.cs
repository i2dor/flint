using System;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Events;
using BTCPayServer.HostedServices;
using BTCPayServer.Payments.Lightning;
using BTCPayServer.Plugins.Flint.Data;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.Flint.Services;

/// <summary>
/// Keeps the plugin's own payment-hash → BTCPay invoice association, from core's prompt-mint event.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists alongside BTCPay's own index.</b> The credit gateway
/// (<c>BTCPayInvoiceCreditGateway.FindByPaymentHashAsync</c>) resolves which invoice a settled payment
/// belongs to through BTCPay's <c>AddressInvoices</c> table — the authority, and insert-only, so a
/// superseded BOLT11 keeps pointing at the invoice it was minted for. But core writes an LNURL prompt's
/// payment hash into that table only when LUD-21 is enabled (<c>UILNURLController</c>), so a store whose
/// merchant disabled LUD-21 by hand has the LNURL half of the association unrecorded in core, and a late
/// payment to a superseded LNURL BOLT11 after a restart would reach the Spark wallet with nothing left
/// tying it to its BTCPay invoice. The event this class subscribes to —
/// <see cref="InvoiceNewPaymentDetailsEvent"/> — is published by core on every prompt mint regardless of
/// LUD-21, so it is the observation point that restores the association independently of that setting.
/// </para>
/// <para>
/// <b>Scope.</b> Only Bitcoin Lightning prompts are recorded (<c>BTC-LN</c> and <c>BTC-LNURL</c>): those
/// are the only payment methods this plugin can credit, and the only ones whose hash the gateway asks
/// about. Prompts without a payment hash yet (the LNURL <em>request</em> event, which precedes the mint
/// that gives a prompt its hash) are skipped; the mint event that follows carries the hash.
/// </para>
/// <para>
/// <b>Gated on the plugin actually serving anyone.</b> The event carries no store id (core's
/// <c>InvoiceNewPaymentDetailsEvent</c> is invoice + details + payment method), and the association this index
/// exists to restore only matters for a store this plugin has provisioned — for every other store core's own
/// <c>AddressInvoices</c> is complete and the write is noise on a server-wide hot path. The gate is therefore
/// the any-store question, read live from <see cref="SparkService"/> on every event: true from the moment the
/// first Flint store is provisioned, without a restart.
/// </para>
/// <para>
/// <b>Best-effort, and why that is the right bound.</b> A failed write is logged and swallowed — it must
/// not unwind the event-aggregator loop. This index is a fallback over core's own table, not the primary
/// authority: for a store with LUD-21 on (which this plugin forces when it provisions a store), core's
/// <c>AddressInvoices</c> row covers the same hashes and this index is redundant. It exists for the
/// LUD-21-off store, and for that store a row can only be written if this plugin was running when the
/// prompt was minted — a prompt minted during a plugin outage leaves no observer, and is reported as
/// unattributable exactly as before.
/// </para>
/// </remarks>
public class SparkInvoicePaymentHashIndexer : EventHostedServiceBase
{
    private readonly IInvoicePaymentHashIndex _index;
    private readonly SparkService _sparkService;
    private readonly ILogger<SparkInvoicePaymentHashIndexer> _logger;

    public SparkInvoicePaymentHashIndexer(
        EventAggregator eventAggregator,
        IInvoicePaymentHashIndex index,
        SparkService sparkService,
        ILogger<SparkInvoicePaymentHashIndexer> logger) : base(eventAggregator, logger)
    {
        _index = index;
        _sparkService = sparkService;
        _logger = logger;
    }

    protected override void SubscribeToEvents()
    {
        // The mint-time association, published by core whether or not LUD-21 is enabled — which is the whole
        // point, see the class remarks.
        Subscribe<InvoiceNewPaymentDetailsEvent>();
        base.SubscribeToEvents();
    }

    protected override Task ProcessEvent(object evt, CancellationToken cancellationToken)
    {
        if (evt is InvoiceNewPaymentDetailsEvent prompt)
            return RecordAssociationAsync(prompt, cancellationToken);
        return base.ProcessEvent(evt, cancellationToken);
    }

    /// <summary>
    /// Records the payment hash → invoice association carried by one prompt-mint event. Never throws.
    /// </summary>
    /// <remarks>
    /// Public so the decision — which events are recorded, and that a database failure cannot unwind the
    /// caller — is unit-testable without driving the whole hosted service. Never throws for the reason the
    /// base class's loop logs-and-continues: a prompt association is not worth derailing the event
    /// aggregator over, and the gateway falls back to this index only when core's own table has nothing.
    /// </remarks>
    public async Task RecordAssociationAsync(
        InvoiceNewPaymentDetailsEvent prompt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(prompt);

        try
        {
            // Only the payment methods this plugin can credit, and only prompts that carry a hash yet. The
            // LNURL request event (UILNURLController, before the first mint) has no hash; the mint event
            // that follows does.
            if (!BTCPayInvoiceCreditGateway.CreditablePaymentMethods.Contains(prompt.PaymentMethodId)
                || prompt.Details is not LigthningPaymentPromptDetails { PaymentHash: not null } details)
            {
                return;
            }

            // The any-store gate, re-read per event rather than cached: provisioning is rare and the settings
            // cache offers no invalidation hook to hang a flag on, while a stale false would silently stop
            // recording associations for a store provisioned since startup. Placed after the payment-method
            // filter so the common skip — a prompt this plugin could never credit — costs no dictionary read.
            if (!await _sparkService.HasAnyStoreProvisioned().ConfigureAwait(false))
            {
                return;
            }

            await _index.RecordAsync(
                new InvoicePaymentHash
                {
                    PaymentHash = details.PaymentHash.ToString().ToLowerInvariant(),
                    InvoiceId = prompt.InvoiceId,
                    PaymentMethodId = prompt.PaymentMethodId.ToString()
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Best-effort by design — see the class remarks for why this bound is correct. The hash's
            // invoice remains resolvable through core's own table whenever LUD-21 is on, which is why the
            // store provisioning forces it on.
            _logger.LogWarning(ex,
                "Could not record which BTCPay invoice minted the prompt for an event on invoice "
                + "{InvoiceId}; a late payment to its BOLT11 will be attributable through this plugin's own "
                + "index only if LUD-21 is enabled",
                prompt.InvoiceId);
        }
    }
}
