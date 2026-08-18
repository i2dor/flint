using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Plugins.Flint.Data;

namespace BTCPayServer.Plugins.Flint.Services;

/// <summary>
/// Labels a sweep's on-chain transaction in the store's BTCPay wallet, so the transactions list says where
/// the money came from.
/// </summary>
/// <remarks>
/// A seam over core's <c>WalletRepository</c> for the same reason <see cref="ISweepAddressSource"/> is one:
/// the repository is a concrete class over the application database, and the engine's economics are unit-tested
/// without either. Implementations must be best-effort — a label that cannot be written must never fail or
/// retry a sweep that has already moved money.
/// </remarks>
public interface ISweepTransactionLabeler
{
    /// <summary>
    /// Labels <paramref name="txId"/> in the wallet of the store that owns <paramref name="record"/>.
    /// </summary>
    /// <param name="record">The sweep the transaction belongs to. Decides whether a label applies at all.</param>
    /// <param name="txId">
    /// The transaction id, passed separately because the caller may hold it before it is mirrored onto the
    /// record — the reconciliation path learns it from the SDK's payment, not from the row.
    /// </param>
    Task LabelAsync(SweepRecord record, string txId, CancellationToken cancellationToken = default);
}
