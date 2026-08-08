using BTCPayServer.Configuration;
using BTCPayServer.Logging;
using BTCPayServer.Plugins.Flint.Sdk;
using BTCPayServer.Plugins.Flint.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NBitcoin;
using NBXplorer;

namespace BTCPayServer.Plugins.Flint.Tests.Fakes;

/// <summary>
/// The real <see cref="SparkService"/>, over fakes, with a connect deadline short enough to test.
/// </summary>
/// <remarks>
/// <para>
/// The class under test rather than a stand-in for it. Its startup loop, its per-store instance lifecycle and
/// its settings cache are all things whose failure modes are invisible to a test that reimplements them —
/// notably the one this exists for: an SDK connect that never returns, on the host's
/// <c>IHostedService.StartAsync</c> path, which has no timeout of its own.
/// </para>
/// <para>
/// Nothing here touches a database or the SDK's native library beyond what <c>SparkService.StartAsync</c>
/// itself does. The store repository is BTCPay's own <c>IStoreRepository</c> contract, faked; every other
/// dependency is the plugin's own type built over fakes the rest of the suite already uses.
/// </para>
/// </remarks>
public sealed class SparkServiceHarness : IDisposable
{
    private readonly string _dataDir;

    private SparkServiceHarness(
        TestableSparkService service,
        FakeStoreRepository stores,
        FakeSparkSdkClientFactory sdk,
        FakeStoreLightningConfigStore lightning,
        SparkMnemonicProtector protector,
        InMemoryInvoiceRecordStore invoices,
        SparkSettlementBroadcaster broadcaster,
        CapturingLogger<SparkService> log,
        string dataDir)
    {
        Service = service;
        Stores = stores;
        Sdk = sdk;
        Lightning = lightning;
        Protector = protector;
        Invoices = invoices;
        Broadcaster = broadcaster;
        Log = log;
        _dataDir = dataDir;
    }

    public SparkService Service { get; }
    public FakeStoreRepository Stores { get; }
    public FakeSparkSdkClientFactory Sdk { get; }
    public FakeStoreLightningConfigStore Lightning { get; }
    public SparkMnemonicProtector Protector { get; }

    /// <summary>The invoice records the service settles against — the far end of the event wiring.</summary>
    public InMemoryInvoiceRecordStore Invoices { get; }

    /// <summary>What a settled invoice is announced on, which is what wakes BTCPay's listening session.</summary>
    public SparkSettlementBroadcaster Broadcaster { get; }

    public CapturingLogger<SparkService> Log { get; }

    /// <summary>The BTCPay data directory this service was given, which is where its per-store storage lives.</summary>
    public string DataDir => _dataDir;

    /// <summary>
    /// Where a store's SDK storage lives, resolved the way the service resolves it.
    /// </summary>
    /// <remarks>
    /// Through the production helper rather than by re-spelling the layout: a test that hard-coded the path
    /// would keep passing after a change that moved it, while the guard it is checking stopped covering
    /// anything.
    /// </remarks>
    public string StorageDirFor(string storeId) =>
        FileSparkStorageProvider.GetStorageDirectory(_dataDir, storeId);

    /// <summary>A valid BIP39 phrase. Distinct phrases matter: the wallet-owner guard keys on the seed.</summary>
    public static string MnemonicFor(int index)
    {
        var entropy = new byte[16];
        entropy[0] = (byte)index;
        return new Mnemonic(Wordlist.English, entropy).ToString();
    }

    public static SparkServiceHarness Create(
        TimeSpan? connectDeadline = null,
        TimeSpan? confirmStatusDeadline = null,
        TimeSpan? abandonedConnectGrace = null)
    {
        var dataDir = Path.Combine(
            Path.GetTempPath(), "spark-service-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataDir);

        var log = new CapturingLogger<SparkService>();
        var logs = new Logs();
        logs.Configure(NullLoggerFactory.Instance);

        var stores = new FakeStoreRepository();
        var sdk = new FakeSparkSdkClientFactory();
        var lightning = FakeStoreLightningConfigStore.WithStore("unused");
        var protector = new SparkMnemonicProtector(new EphemeralDataProtectionProvider());

        var broadcaster = new SparkSettlementBroadcaster(NullLogger<SparkSettlementBroadcaster>.Instance);
        var invoices = new InMemoryInvoiceRecordStore();
        var reconciler = new SparkSettlementReconciler(
            invoices, broadcaster, NullLogger<SparkSettlementReconciler>.Instance);

        var service = new TestableSparkService(
            connectDeadline ?? TimeSpan.FromMilliseconds(250),
            confirmStatusDeadline ?? Constants.SdkCallDeadline,
            // Long by default so the existing abandon tests still observe a late connect being adopted and shut
            // down; the release-the-lock test shortens it deliberately.
            abandonedConnectGrace ?? TimeSpan.FromMinutes(5),
            new BTCPayServer.EventAggregator(logs),
            stores,
            Options.Create(new DataDirectories { DataDir = dataDir }),
            new BTCPayNetworkProvider([], new NBXplorerNetworkProvider(ChainName.Regtest), logs),
            sdk,
            invoices,
            new InMemoryOutgoingPaymentStore(),
            reconciler,
            broadcaster,
            protector,
            new SparkLightningWiring(lightning, NullLogger<SparkLightningWiring>.Instance),
            new StubBolt11Parser(),
            TimeProvider.System,
            NullLoggerFactory.Instance,
            log);

        return new SparkServiceHarness(
            service, stores, sdk, lightning, protector, invoices, broadcaster, log, dataDir);
    }

    /// <summary>Stores a store's Spark settings the way a previous run would have left them.</summary>
    public SparkServiceHarness SeedStore(string storeId, string mnemonic, string? paymentKey = null)
    {
        Stores.Seed(storeId, Constants.StoreSettingsKey, new SparkSettings
        {
            ProtectedMnemonic = Protector.Protect(mnemonic),
            PaymentKey = paymentKey ?? SparkConnectionString.GeneratePaymentKey(),
            SeedSource = SeedSource.Generated
        });
        return this;
    }

    public void Dispose()
    {
        // Bounded, because StopAsync takes the same instance lock the startup loop holds. If a regression has
        // put a never-returning await back on the startup path, that lock is held forever and an unbounded
        // teardown here would turn a failed assertion into a hung test run — hiding the very failure the
        // assertion just reported.
        try
        {
            Service.StopAsync(CancellationToken.None).Wait(TimeSpan.FromSeconds(10));
        }
        catch (Exception)
        {
            // Teardown of a service that never fully started is not worth failing a test over.
        }

        try
        {
            Directory.Delete(_dataDir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>
    /// <see cref="SparkService"/> with the two SDK deadlines shortened so a test can wait them out.
    /// </summary>
    /// <remarks>
    /// The only things overridden, and only their durations. Everything the tests assert on — the startup loop,
    /// the abandonment, the settings cache, the whole event-consumption path — is the production
    /// implementation. Both default to the production value, so a test that does not care about a deadline is
    /// running the shipped one rather than a convenient one.
    /// </remarks>
    private sealed class TestableSparkService : SparkService
    {
        private readonly TimeSpan _connectDeadline;
        private readonly TimeSpan _confirmStatusDeadline;
        private readonly TimeSpan _abandonedConnectGrace;

        public TestableSparkService(
            TimeSpan connectDeadline,
            TimeSpan confirmStatusDeadline,
            TimeSpan abandonedConnectGrace,
            BTCPayServer.EventAggregator eventAggregator,
            BTCPayServer.Abstractions.Contracts.IStoreRepository storeRepository,
            IOptions<DataDirectories> dataDirectories,
            BTCPayNetworkProvider networkProvider,
            ISparkSdkClientFactory sdkClientFactory,
            Data.IInvoiceRecordStore invoiceStore,
            Data.IOutgoingPaymentStore outgoingStore,
            SparkSettlementReconciler reconciler,
            SparkSettlementBroadcaster broadcaster,
            SparkMnemonicProtector mnemonicProtector,
            SparkLightningWiring lightningWiring,
            IBolt11Parser bolt11Parser,
            TimeProvider timeProvider,
            ILoggerFactory loggerFactory,
            ILogger<SparkService> logger)
            : base(eventAggregator, storeRepository, dataDirectories, networkProvider, sdkClientFactory,
                invoiceStore, outgoingStore, reconciler, broadcaster, mnemonicProtector, lightningWiring,
                bolt11Parser, timeProvider, loggerFactory, logger)
        {
            _connectDeadline = connectDeadline;
            _confirmStatusDeadline = confirmStatusDeadline;
            _abandonedConnectGrace = abandonedConnectGrace;
        }

        protected override TimeSpan ConnectDeadline => _connectDeadline;
        protected override TimeSpan ConfirmStatusDeadline => _confirmStatusDeadline;
        protected override TimeSpan AbandonedConnectGraceDeadline => _abandonedConnectGrace;
    }
}
