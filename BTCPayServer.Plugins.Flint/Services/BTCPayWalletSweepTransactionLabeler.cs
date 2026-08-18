using System;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Data;
using BTCPayServer.Plugins.Flint.Data;
using BTCPayServer.Services;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.Flint.Services;

/// <summary>
/// The real <see cref="ISweepTransactionLabeler"/>, over core's <c>WalletRepository</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the label goes on the transaction and not only on the address.</b> The reserving path already writes
/// <c>generatedBy: "Spark sweep"</c> onto the address object, but the wallet's transactions list looks up wallet
/// objects of type <c>tx</c> alone — address metadata never reaches those rows (only the coin-selection view
/// merges it). So without this, a sweep lands in the store's Bitcoin wallet as an anonymous incoming
/// transaction, which for money that moved on its own is the wrong default. This writes the same kind of
/// transaction attachment core's payouts and invoices write, so the row gets a <c>flint-sweep</c> chip with a
/// tooltip, filterable like any other label.
/// </para>
/// <para>
/// <b>Best-effort by contract.</b> Every call site runs after money has already moved; a labelling failure is
/// logged and swallowed, because there is no version of "retry the sweep" that makes a missing label worth it.
/// The write itself is idempotent — <c>EnsureCreated</c> ignores rows that already exist — so the two resolution
/// paths (fresh send, crash reconciliation) and a Sent→Confirmed promotion can all call it for one transaction.
/// </para>
/// </remarks>
public sealed class BTCPayWalletSweepTransactionLabeler : ISweepTransactionLabeler
{
    /// <summary>Spark is Bitcoin-only, and so is every transaction this labels.</summary>
    private const string CryptoCode = "BTC";

    /// <summary>
    /// The attachment type, which is also the chip text in the wallet's transactions list and the label a
    /// merchant can filter by. Kebab-case like core's own (<c>payout</c>, <c>payment-request</c>).
    /// </summary>
    internal const string AttachmentType = "flint-sweep";

    /// <summary>Shown when the merchant hovers the chip. Core's generic tag renderer reads it from the data.</summary>
    internal const string Tooltip = "Swept from this store's Spark wallet by the Flint plugin";

    private readonly WalletRepository _walletRepository;
    private readonly ILogger<BTCPayWalletSweepTransactionLabeler> _logger;

    public BTCPayWalletSweepTransactionLabeler(
        WalletRepository walletRepository,
        ILogger<BTCPayWalletSweepTransactionLabeler> logger)
    {
        _walletRepository = walletRepository;
        _logger = logger;
    }

    public async Task LabelAsync(SweepRecord record, string txId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentException.ThrowIfNullOrEmpty(txId);

        // A cross-chain delivery never appears in the store's Bitcoin wallet, so there is no transaction row
        // for a label to decorate — the provider's Spark-side transfer is not an on-chain transaction at all.
        //
        // Both Bitcoin destination modes are labelled, deliberately. The label is a row keyed by txid that only
        // ever renders when the wallet actually contains the transaction: a sweep into the store's wallet always
        // does, and a sweep to a fixed address does exactly when that address turns out to be one the store's
        // wallet watches — which is when a merchant would want the label most. For a genuinely external address
        // the row is inert, which costs nothing.
        if (record.DestinationKind is not SweepDestinationKind.BitcoinAddress)
            return;

        try
        {
            // The id makes the sweep findable from the wallet object later; the tooltip is what the merchant
            // reads. No link, deliberately: this runs on a background pass with no request context, and a
            // hand-built root-relative URL would break under a non-root path base.
            await _walletRepository.AddWalletTransactionAttachment(
                    new WalletId(record.StoreId, CryptoCode),
                    txId,
                    [new Attachment(AttachmentType, record.IdempotencyKey, new JObject { ["tooltip"] = Tooltip })],
                    WalletObjectData.Types.Tx)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Store {StoreId}: could not label sweep transaction {TxId} in the store's wallet",
                record.StoreId, txId);
        }
    }
}
