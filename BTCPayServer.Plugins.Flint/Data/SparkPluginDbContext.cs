using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace BTCPayServer.Plugins.Flint.Data;

/// <summary>
/// The plugin's EF Core context. Lives in its own Postgres schema
/// (<see cref="Constants.DatabaseSchema"/>) so it never collides with BTCPay's own tables and can
/// be migrated independently, which is the convention BTCPay plugins follow.
/// </summary>
public class SparkPluginDbContext : DbContext
{
    public DbSet<InvoiceRecord> InvoiceRecords { get; set; } = null!;
    public DbSet<OutgoingPaymentRecord> OutgoingPayments { get; set; } = null!;
    public DbSet<SweepRecord> SweepRecords { get; set; } = null!;
    public DbSet<InvoicePaymentHash> InvoicePaymentHashes { get; set; } = null!;

    public SparkPluginDbContext(DbContextOptions<SparkPluginDbContext> options) : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.HasDefaultSchema(Constants.DatabaseSchema);

        modelBuilder.Entity<InvoiceRecord>(entity =>
        {
            // The payment hash is the primary key, so BTCPay's per-invoice GetInvoice(id) lookups and the
            // reconciliation task's per-invoice resolution are both point reads on the PK index.
            entity.HasKey(record => record.PaymentHash);
            // Every read path is store-scoped, and expiry sweeps scan by store + status.
            entity.HasIndex(record => new { record.StoreId, record.Status });
            // ListInvoices pages newest-first within a store.
            entity.HasIndex(record => new { record.StoreId, record.CreatedAt });
            // The every-minute cross-store credit walk (ListStoreIdsAwaitingCreditAsync) and the
            // per-store ListUncreditedAsync both filter on exactly this predicate. Without it the
            // cross-store DISTINCT scans every InvoiceRecords row each pass; with it both queries are
            // index-only over a partial index that holds roughly the unsettled tail of the table.
            // Quoted identifiers: the columns are created mixed-case and Postgres would otherwise
            // fold the filter's references to lowercase and fail to match.
            entity.HasIndex(record => new { record.StoreId, record.SettledAt })
                .HasFilter("\"Status\" = 1 AND \"CreditedAt\" IS NULL AND \"CreditAbandonedAt\" IS NULL AND \"SettledAt\" IS NOT NULL");
        });

        modelBuilder.Entity<OutgoingPaymentRecord>(entity =>
        {
            // Keyed on store <b>and</b> payment hash, matching every query. A payment-hash-only key would be a
            // silent cross-store defect: two stores on one server can each be asked to pay the same BOLT11 (a
            // shared supplier invoice, say), and the second store's insert would collide with the first store's
            // row, while the store-scoped read that follows would find nothing. The synthesized fallback record
            // would then report ReportedAt = null forever, so a legitimate crash-retry of a payment that had
            // already been sent would be reported to BTCPay as a failure.
            entity.HasKey(record => new { record.StoreId, record.PaymentHash });
            entity.HasIndex(record => new { record.StoreId, record.FirstAttemptAt });
        });

        modelBuilder.Entity<SweepRecord>(entity =>
        {
            // The SDK's idempotency key is the primary key, which is what makes "one sweep per key" a database
            // guarantee rather than a convention: the insert that precedes every send would collide.
            entity.HasKey(record => record.IdempotencyKey);
            // The history page pages newest-first within a store.
            entity.HasIndex(record => new { record.StoreId, record.CreatedAt });
            // Every pass of the sweep engine opens by looking for this store's in-flight rows.
            entity.HasIndex(record => new { record.StoreId, record.Status });
        });

        modelBuilder.Entity<InvoicePaymentHash>(entity =>
        {
            // The payment hash is the primary key — the read path is a point read on it, exactly as in core's
            // AddressInvoices. No store-scoped key: a hash is unique to one invoice, and the reader never
            // filters by store.
            entity.HasKey(record => record.PaymentHash);
            // The table name is pinned so the raw SQL in EfInvoicePaymentHashIndex.RecordAsync names the same
            // table EF creates, rather than relying on both ending up at EF's default pluralisation.
            entity.ToTable("InvoicePaymentHashes");
        });
    }
}

/// <summary>
/// Design-time factory, used only by <c>dotnet ef</c> when authoring migrations. The connection
/// string is never used at runtime; it only has to be a valid Npgsql string so the provider can
/// build a model. Matches the pattern in BTCPay's other plugins.
/// </summary>
public class SparkPluginDesignTimeDbContextFactory : IDesignTimeDbContextFactory<SparkPluginDbContext>
{
    public SparkPluginDbContext CreateDbContext(string[] args)
    {
        var builder = new DbContextOptionsBuilder<SparkPluginDbContext>();
        builder.UseNpgsql("User ID=postgres;Host=127.0.0.1;Port=39372;Database=designtimebtcpay");
        return new SparkPluginDbContext(builder.Options);
    }
}
