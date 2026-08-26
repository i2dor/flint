using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;

namespace BTCPayServer.Plugins.Flint.Data;

/// <summary>
/// <see cref="IInvoicePaymentHashIndex"/> over the plugin's own Postgres schema.
/// </summary>
/// <remarks>
/// <para>
/// One short-lived <see cref="SparkPluginDbContext"/> per operation, created from
/// <see cref="SparkPluginDbContextFactory"/> — the same convention and the same reason as
/// <see cref="EfInvoiceRecordStore"/>: the writer is called from the event-aggregator loop and the reader
/// from BTCPay's invoice credit path, neither of which is inside an HTTP request scope.
/// </para>
/// <para>
/// The write is a single <c>INSERT ... ON CONFLICT DO NOTHING</c> statement, for the two reasons every other
/// statement in this schema is single and unconditional: this context's retrying execution strategy refuses
/// user-initiated transactions (see <see cref="EfInvoiceRecordStore"/> for the reproduced failure), and the
/// association is write-once — a hash can never legitimately change invoice — so the database deciding on
/// conflict is the whole semantics. It mirrors core's <c>UpsertAddressInvoice</c>, which keeps the same table
/// this one mirrors for the same reason.
/// </para>
/// </remarks>
public class EfInvoicePaymentHashIndex : IInvoicePaymentHashIndex
{
    private readonly SparkPluginDbContextFactory _contextFactory;

    public EfInvoicePaymentHashIndex(SparkPluginDbContextFactory contextFactory)
    {
        _contextFactory = contextFactory;
    }

    public async Task RecordAsync(InvoicePaymentHash entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentException.ThrowIfNullOrEmpty(entry.PaymentHash);
        ArgumentException.ThrowIfNullOrEmpty(entry.InvoiceId);
        ArgumentException.ThrowIfNullOrEmpty(entry.PaymentMethodId);

        await using var context = _contextFactory.CreateContext();

        // Normalised here rather than trusted from the caller: the event carries the hash in whatever case
        // core produced it, and this table's primary key is case-sensitive.
        var paymentHash = entry.PaymentHash.ToLowerInvariant();
        var firstSeenAt = entry.FirstSeenAt == default ? DateTimeOffset.UtcNow : entry.FirstSeenAt;

        await context.Database.ExecuteSqlInterpolatedAsync(
            $"""
             INSERT INTO "{Constants.DatabaseSchema}"."InvoicePaymentHashes"
                 ("PaymentHash", "InvoiceId", "PaymentMethodId", "FirstSeenAt")
             VALUES ({paymentHash}, {entry.InvoiceId}, {entry.PaymentMethodId}, {firstSeenAt})
             ON CONFLICT ("PaymentHash") DO NOTHING
             """,
            cancellationToken);
    }

    public async Task<InvoicePaymentHash?> FindByPaymentHashAsync(
        string paymentHash,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(paymentHash);

        await using var context = _contextFactory.CreateContext();
        return await context.InvoicePaymentHashes
            .AsNoTracking()
            .FirstOrDefaultAsync(h => h.PaymentHash == paymentHash.ToLowerInvariant(), cancellationToken);
    }
}
