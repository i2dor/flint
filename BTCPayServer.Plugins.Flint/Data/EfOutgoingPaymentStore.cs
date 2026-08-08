using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;

namespace BTCPayServer.Plugins.Flint.Data;

/// <summary>
/// <see cref="IOutgoingPaymentStore"/> over the plugin's own Postgres schema.
/// </summary>
/// <remarks>
/// As in <see cref="EfInvoiceRecordStore"/>, nothing here may open an explicit transaction: the shared
/// context factory enables retry-on-failure, and EF's retrying execution strategy refuses user-initiated
/// transactions. Atomicity comes from single conditional statements.
/// </remarks>
public class EfOutgoingPaymentStore : IOutgoingPaymentStore
{
    private readonly SparkPluginDbContextFactory _contextFactory;

    public EfOutgoingPaymentStore(SparkPluginDbContextFactory contextFactory)
    {
        _contextFactory = contextFactory;
    }

    public async Task<OutgoingPaymentRecord> RegisterAttemptAsync(
        string storeId,
        string paymentHash,
        string idempotencyKey,
        string bolt11,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(storeId);
        ArgumentException.ThrowIfNullOrEmpty(paymentHash);
        ArgumentException.ThrowIfNullOrEmpty(idempotencyKey);

        await using var context = _contextFactory.CreateContext();

        // Increment first. If the row exists this is one atomic statement and no insert is attempted; if it
        // does not, the insert below runs and its primary key is the race guard.
        var updated = await context.OutgoingPayments
            .Where(r => r.PaymentHash == paymentHash && r.StoreId == storeId)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(r => r.AttemptCount, r => r.AttemptCount + 1),
                cancellationToken);

        if (updated == 0)
        {
            var created = new OutgoingPaymentRecord
            {
                PaymentHash = paymentHash,
                StoreId = storeId,
                IdempotencyKey = idempotencyKey,
                Bolt11 = bolt11,
                FirstAttemptAt = now,
                AttemptCount = 1
            };
            context.OutgoingPayments.Add(created);
            try
            {
                await context.SaveChangesAsync(cancellationToken);
                return created;
            }
            catch (DbUpdateException)
            {
                // Another caller inserted the same payment hash between the update and the insert. Fall
                // through to the read below, which returns their row; losing this attempt's increment is
                // immaterial next to reporting the wrong ReportedAt.
                context.ChangeTracker.Clear();
            }
        }

        var record = await context.OutgoingPayments
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.PaymentHash == paymentHash && r.StoreId == storeId, cancellationToken);

        // Only unreachable in practice: nothing deletes these rows. Synthesising one keeps the caller's
        // contract non-nullable rather than making every call site handle an impossible case.
        return record ?? new OutgoingPaymentRecord
        {
            PaymentHash = paymentHash,
            StoreId = storeId,
            IdempotencyKey = idempotencyKey,
            Bolt11 = bolt11,
            FirstAttemptAt = now,
            AttemptCount = 1
        };
    }

    public async Task<bool> TryMarkReportedAsync(
        string storeId,
        string paymentHash,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        await using var context = _contextFactory.CreateContext();

        // The compare-and-set that makes "exactly one caller may report this payment as sent" true.
        var updated = await context.OutgoingPayments
            .Where(r => r.PaymentHash == paymentHash && r.StoreId == storeId && r.ReportedAt == null)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(r => r.ReportedAt, now),
                cancellationToken);
        return updated == 1;
    }
}
