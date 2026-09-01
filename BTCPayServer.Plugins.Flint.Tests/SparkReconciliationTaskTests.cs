using BTCPayServer.Plugins.Flint.Data;
using BTCPayServer.Plugins.Flint.Services;
using BTCPayServer.Plugins.Flint.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BTCPayServer.Plugins.Flint.Tests;

/// <summary>
/// The scheduled reconciliation pass. The cross-store configuration sweep used to ride this pass at the
/// one-minute reconciliation cadence; it now runs on its own slower task, driven by
/// <see cref="SparkLightningConfigSweepTask"/> and covered by
/// <see cref="SparkLightningConfigSweepTaskTests"/> — the sweep reloads every store, and the save-time
/// refusal means it only has to catch configurations written outside HTTP. What remains under test here is
/// that <see cref="SparkReconciliationTask.Do"/> drives the settlement walk and a repeat pass stays quiet:
/// the sweep no longer reaches this task at all, and cannot — its constructor no longer has one.
/// </summary>
public class SparkReconciliationTaskTests
{
    private const string StoreId = "reconciliation-task-store";
    private const string InvoiceId = "btcpay-invoice-1";
    private const string Bolt11 = "lnbcrt-1";

    private static readonly string Hash = PaymentFixture.PaymentHash;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact(Timeout = 30_000)]
    public async Task A_periodic_pass_credits_what_it_finds_and_repeats_harmlessly()
    {
        using var harness = SparkServiceHarness.Create();
        harness.SeedStore(StoreId, SparkServiceHarness.MnemonicFor(1));

        // A settlement committed before the process came up: paid, uncredited, and nothing will ever notify
        // anyone about it again — the pass exists to find exactly this.
        harness.Invoices.Seed(new InvoiceRecord
        {
            PaymentHash = Hash,
            StoreId = StoreId,
            Bolt11 = Bolt11,
            AmountMsat = 100_000,
            AmountReceivedMsat = 100_000,
            SdkPaymentId = "recv-1",
            Preimage = PaymentFixture.Preimage,
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-30),
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-20),
            SettledAt = DateTimeOffset.UtcNow.AddMinutes(-25),
            Status = InvoiceRecordStatus.Paid
        });
        harness.Credits.Mint(Hash, InvoiceId, StoreId);

        await harness.Service.StartAsync(Ct);

        // The startup pass reaches it first (the same fire-and-forget pass SparkService runs); wait for that
        // rather than race it, so the periodic call below runs against an already-settled world.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (harness.Invoices.Records[Hash].CreditedAt is null)
        {
            if (DateTime.UtcNow > deadline)
                Assert.Fail("the settlement recorded before startup was never credited");
            await Task.Delay(10, Ct);
        }

        var task = new SparkReconciliationTask(
            harness.Service,
            NullLogger<SparkReconciliationTask>.Instance);

        await task.Do(Ct);

        // A repeat pass re-credits nothing: the row is stamped, and the walk skips it.
        Assert.Single(harness.Credits.CreditsFor(InvoiceId));
    }
}
