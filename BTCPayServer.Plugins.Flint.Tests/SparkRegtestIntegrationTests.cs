using System.Threading.Channels;
using BTCPayServer.Lightning;
using BTCPayServer.Plugins.Flint.Sdk;
using BTCPayServer.Plugins.Flint.Services;
using BTCPayServer.Plugins.Flint.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using NBitcoin;
using Xunit;
using SdkNetwork = Breez.Sdk.Spark.Network;

namespace BTCPayServer.Plugins.Flint.Tests;

/// <summary>
/// Opt-in smoke test against the Lightspark-hosted regtest.
/// </summary>
/// <remarks>
/// <para>
/// Skipped unless <c>SPARK_INTEGRATION_TESTS=1</c>, because it loads the SDK's ~200 MB native library,
/// talks to a real service provider over the network, and writes a SQLite file. Nothing here needs an API
/// key: regtest accepts a null one (and, unhelpfully, accepts a garbage one too, which is why the key path
/// can only be validated on mainnet).
/// </para>
/// <para>
/// What it covers is the whole invoice-creation path end to end against the real SSP — the part that
/// carries every silent coercion in the SDK's receive request. It cannot cover settlement: paying these
/// invoices needs a regtest Lightning sender, and a funded wallet.
/// </para>
/// <code>SPARK_INTEGRATION_TESTS=1 dotnet test</code>
/// </remarks>
[Trait("Category", "Integration")]
public class SparkRegtestIntegrationTests
{
    private static bool Enabled =>
        Environment.GetEnvironmentVariable("SPARK_INTEGRATION_TESTS") == "1";

    [Fact]
    public async Task Connect_and_create_a_real_regtest_invoice()
    {
        Assert.SkipUnless(Enabled, "Set SPARK_INTEGRATION_TESTS=1 to run the Spark regtest smoke test.");

        var storageDir = Path.Combine(Path.GetTempPath(), "spark-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(storageDir);
        var events = Channel.CreateBounded<SparkEventEnvelope>(new BoundedChannelOptions(64));

        var factory = new SparkSdkClientFactory(
            new FixedStorageProvider(storageDir),
            new NBitcoinBolt11Parser(Network.RegTest, NullLogger<NBitcoinBolt11Parser>.Instance),
            NullLoggerFactory.Instance);

        // A throwaway wallet. Nothing is funded, so the seed has no value.
        var mnemonic = new Mnemonic(Wordlist.English, WordCount.Twelve).ToString();

        ISparkSdkClient? sdk = null;
        try
        {
            sdk = await factory.ConnectAsync(
                new SparkConnectOptions("integration-store", mnemonic, null, apiKey: null, SdkNetwork.Regtest),
                events.Writer,
                TestContext.Current.CancellationToken);

            var store = new InMemoryInvoiceRecordStore();
            var broadcaster = new SparkSettlementBroadcaster(NullLogger<SparkSettlementBroadcaster>.Instance);
            var client = new SparkLightningClient(
                "integration-store",
                "integration-key",
                sdk,
                store,
                new InMemoryOutgoingPaymentStore(),
                new SparkSettlementReconciler(
                    store, broadcaster, NullLogger<SparkSettlementReconciler>.Instance),
                broadcaster,
                new NBitcoinBolt11Parser(Network.RegTest, NullLogger<NBitcoinBolt11Parser>.Instance),
                NullLogger.Instance);

            var invoice = await client.CreateInvoice(
                new CreateInvoiceParams(LightMoney.Satoshis(1000), "spark plugin smoke test", TimeSpan.FromMinutes(15)),
                TestContext.Current.CancellationToken);

            Assert.StartsWith("lnbcrt", invoice.BOLT11);
            Assert.Equal(64, invoice.Id.Length);
            Assert.Equal(invoice.Id, invoice.PaymentHash);
            Assert.Equal(LightningInvoiceStatus.Unpaid, invoice.Status);
            Assert.Equal(1_000_000, invoice.Amount.MilliSatoshi);
            // The SSP honours the expiry we asked for rather than substituting its own default.
            Assert.InRange(
                invoice.ExpiresAt,
                DateTimeOffset.UtcNow.AddMinutes(10),
                DateTimeOffset.UtcNow.AddMinutes(20));

            // The invoice exists in the plugin's table and nowhere else: the SDK keeps no record of an
            // unpaid invoice, which is the entire reason that table exists.
            Assert.Single(store.Records);
            Assert.Empty(await sdk.ListPaymentsAsync(
                new SparkListPaymentsQuery(Limit: 50), TestContext.Current.CancellationToken));

            // And it round-trips through GetInvoice, still unpaid.
            var fetched = await client.GetInvoice(invoice.Id, TestContext.Current.CancellationToken);
            Assert.Equal(LightningInvoiceStatus.Unpaid, fetched.Status);

            // Balance is readable on a fresh wallet, and zero.
            var balance = await client.GetBalance(TestContext.Current.CancellationToken);
            Assert.Equal(LightMoney.Zero, balance.OffchainBalance.Local);

            Assert.Null(await client.Validate());
        }
        finally
        {
            if (sdk is not null)
            {
                // Disconnect then Dispose: after Disconnect alone the instance still mints live invoices.
                await sdk.DisconnectAsync();
                sdk.Dispose();
            }

            try
            {
                Directory.Delete(storageDir, recursive: true);
            }
            catch (IOException)
            {
                // A leftover temp directory is not worth failing a test over.
            }
        }
    }

    [Fact]
    public async Task The_cooperative_exit_path_classifies_the_real_SDKs_errors()
    {
        // What the fake SDK cannot prove. The sweep engine's decisions rest on three classifications of a
        // cooperative-exit failure — "not enough sats", "rejected locally, nothing was sent", and "anything else,
        // so the outcome is unknown" — and the SDK reports all of them as prose inside typed exceptions whose
        // shapes are the plugin's own inference. If a pre-1.0 release changes the wording, the engine starts
        // recording clean refusals as unknown outcomes and blocking sweeps behind them.
        //
        // The wallet is unfunded, which is what makes this test cheap: no exit is ever broadcast.
        Assert.SkipUnless(Enabled, "Set SPARK_INTEGRATION_TESTS=1 to run the Spark regtest smoke test.");

        var storageDir = Path.Combine(Path.GetTempPath(), "spark-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(storageDir);
        var events = Channel.CreateBounded<SparkEventEnvelope>(new BoundedChannelOptions(64));

        var factory = new SparkSdkClientFactory(
            new FixedStorageProvider(storageDir),
            new NBitcoinBolt11Parser(Network.RegTest, NullLogger<NBitcoinBolt11Parser>.Instance),
            NullLoggerFactory.Instance);

        var mnemonic = new Mnemonic(Wordlist.English, WordCount.Twelve).ToString();
        var ct = TestContext.Current.CancellationToken;

        ISparkSdkClient? sdk = null;
        try
        {
            sdk = await factory.ConnectAsync(
                new SparkConnectOptions("integration-store", mnemonic, null, apiKey: null, SdkNetwork.Regtest),
                events.Writer,
                ct);

            // Insufficient funds. This is the classification that decides whether a sweep is recorded as a clean
            // failure the store may retry, or as an unknown outcome that blocks every later sweep.
            const string regtestAddress = "bcrt1qtxwcjjvf4ny9wsw9emgnpazey2vde3xhnyqpw0";
            var noFunds = await Assert.ThrowsAnyAsync<Exception>(
                () => sdk!.QuoteOnchainSendAsync(regtestAddress, 5_000, feesIncluded: false, ct));
            Assert.True(
                SparkErrors.IsInsufficientFunds(noFunds),
                $"an unfunded cooperative exit was not classified as insufficient funds: {noFunds}");
            Assert.DoesNotContain("@v1=", SparkErrors.Describe(noFunds));

            // Rejected locally: definitively nothing sent, so the engine may refuse rather than block.
            var malformed = await Assert.ThrowsAnyAsync<Exception>(
                () => sdk!.QuoteOnchainSendAsync("not-an-address", 5_000, feesIncluded: false, ct));
            Assert.True(
                SparkErrors.IsInvalidInput(malformed),
                $"a malformed destination was not classified as invalid input: {malformed}");

            // And a mainnet address is refused by the SDK too — after the plugin's own check, which is what turns
            // this from an opaque failure into a sentence a merchant can act on.
            const string mainnetAddress = "bc1qar0srrr7xfkvy5l643lydnw9re59gtzzwf5mdq";
            Assert.False(SweepDestinationResolver.TryParse(mainnetAddress, Network.RegTest, out _));
            var wrongNetwork = await Assert.ThrowsAnyAsync<Exception>(
                () => sdk!.QuoteOnchainSendAsync(mainnetAddress, 5_000, feesIncluded: false, ct));
            Assert.DoesNotContain("@v1=", SparkErrors.Describe(wrongNetwork));
        }
        finally
        {
            if (sdk is not null)
            {
                await sdk.DisconnectAsync();
                sdk.Dispose();
            }

            try
            {
                Directory.Delete(storageDir, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private sealed class FixedStorageProvider : ISparkStorageProvider
    {
        private readonly string _path;

        public FixedStorageProvider(string path) => _path = path;

        public SparkStorageTarget GetTarget(string storeId) => new SparkStorageTarget.Directory(_path);
    }
}
