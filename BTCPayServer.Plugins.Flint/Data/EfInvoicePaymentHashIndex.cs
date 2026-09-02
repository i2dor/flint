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

        // ExecuteSqlRawAsync, deliberately, not the interpolated overload: a FormattableString turns every
        // {hole} into a bind parameter, and an identifier cannot be bound — the generated INSERT would read
        // relation "@p0"."InvoicePaymentHashes" where Postgres expects the schema name as a literal (the
        // Postgres contract suite caught exactly that). The identifier is therefore concatenated as text and
        // only the values ride as parameters.
        var table = "\"" + Constants.DatabaseSchema + "\".\"InvoicePaymentHashes\"";
        var sql =
            "INSERT INTO " + table + " (\"PaymentHash\", \"InvoiceId\", \"PaymentMethodId\", \"FirstSeenAt\") "
            + "VALUES ({0}, {1}, {2}, {3}) ON CONFLICT (\"PaymentHash\") DO NOTHING";
        // The IEnumerable<object> overload, not the params one, so the trailing cancellation token is taken
        // as the token rather than as a fifth parameter value.
        await context.Database.ExecuteSqlRawAsync(
            sql,
            new object[] { paymentHash, entry.InvoiceId, entry.PaymentMethodId, firstSeenAt },
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

    /// <remarks>
    /// <c>ExecuteDeleteAsync</c> rather than load-and-<c>Remove</c>: the retrying execution strategy refuses
    /// user-initiated transactions (the class remarks above), and set-delete is the whole operation anyway —
    /// no row is examined first, so there is nothing for a context to track. It emits the single
    /// <c>DELETE ... WHERE "FirstSeenAt" &lt; {cutoff}</c> the <c>FirstSeenAt</c> index exists to serve; the
    /// Postgres contract suite pins that it deletes exactly the rows the cutoff selects.
    /// </remarks>
    public async Task<int> PruneBeforeAsync(
        DateTimeOffset cutoff,
        CancellationToken cancellationToken = default)
    {
        await using var context = _contextFactory.CreateContext();
        return await context.InvoicePaymentHashes
            .Where(hash => hash.FirstSeenAt < cutoff)
            .ExecuteDeleteAsync(cancellationToken);
    }
}
