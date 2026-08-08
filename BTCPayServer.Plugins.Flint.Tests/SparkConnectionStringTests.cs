using BTCPayServer.Lightning;
using BTCPayServer.Plugins.Flint.Services;
using NBitcoin;
using Xunit;

namespace BTCPayServer.Plugins.Flint.Tests;

public class SparkConnectionStringTests
{
    [Fact]
    public void Parse_ignores_other_providers()
    {
        var result = SparkConnectionString.Parse(
            "type=lnd-rest;server=https://127.0.0.1:8080/;macaroon=ab", out var storeId, out var key, out var error);

        Assert.Equal(SparkConnectionStringParseResult.NotOurs, result);
        Assert.Null(storeId);
        Assert.Null(key);
        // No error, so BTCPay keeps offering the string to the handler that does own it.
        Assert.Null(error);
    }

    [Fact]
    public void Parse_accepts_our_type_case_insensitively()
    {
        var result = SparkConnectionString.Parse(
            "type=FlInT;store-id=abc;key=deadbeef", out var storeId, out var key, out var error);

        Assert.Equal(SparkConnectionStringParseResult.Ok, result);
        Assert.Equal("abc", storeId);
        Assert.Equal("deadbeef", key);
        Assert.Null(error);
    }

    [Theory]
    [InlineData("type=flint;key=deadbeef", "store-id")]
    [InlineData("type=flint;store-id=abc", "key")]
    [InlineData("type=flint;store-id=;key=deadbeef", "store-id")]
    [InlineData("type=flint;store-id=abc;key=", "key")]
    public void Parse_reports_missing_components(string connectionString, string missingKey)
    {
        var result = SparkConnectionString.Parse(connectionString, out _, out _, out var error);

        Assert.Equal(SparkConnectionStringParseResult.Invalid, result);
        Assert.Contains(missingKey, error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("nonsense")]
    [InlineData("store-id=abc;key=deadbeef")]
    [InlineData("type=flint;store-id=a;store-id=b;key=c")]
    public void Parse_does_not_throw_on_malformed_input(string connectionString)
    {
        // ExtractValues throws FormatException for a missing type, a duplicate key or a component with no
        // '='. Claiming those as ours would mask the real problem, and letting the exception out would
        // break every other handler in BTCPay's chain.
        var result = SparkConnectionString.Parse(connectionString, out _, out _, out var error);

        Assert.Equal(SparkConnectionStringParseResult.NotOurs, result);
        Assert.Null(error);
    }

    [Fact]
    public void Format_round_trips_through_Parse()
    {
        var key = SparkConnectionString.GeneratePaymentKey();
        var connectionString = SparkConnectionString.Format("store-1", key);

        var result = SparkConnectionString.Parse(connectionString, out var storeId, out var parsedKey, out _);

        Assert.Equal(SparkConnectionStringParseResult.Ok, result);
        Assert.Equal("store-1", storeId);
        Assert.Equal(key, parsedKey);
    }

    [Fact]
    public void Format_contains_no_server_component()
    {
        // BTCPay's IsSafe check rejects a connection string with a server= component unless the user is an
        // admin. Keeping it out is what lets a store owner save their own Spark configuration.
        var connectionString = SparkConnectionString.Format("store-1", "deadbeef");

        Assert.DoesNotContain("server=", connectionString);
        Assert.True(LightningConnectionStringHelper.ExtractValues(connectionString, out _)
            .ContainsKey("store-id"));
    }

    [Fact]
    public void GeneratePaymentKey_produces_unpredictable_256_bit_keys()
    {
        var keys = Enumerable.Range(0, 32).Select(_ => SparkConnectionString.GeneratePaymentKey()).ToList();

        Assert.All(keys, key => Assert.Equal(64, key.Length));
        Assert.Equal(keys.Count, keys.Distinct().Count());
    }

    [Theory]
    [InlineData("abc", "abc", true)]
    [InlineData("abc", "abd", false)]
    [InlineData("abc", "ab", false)]
    [InlineData("abc", "", false)]
    [InlineData(null, "abc", false)]
    [InlineData("abc", null, false)]
    [InlineData(null, null, false)]
    public void PaymentKeyMatches_compares_exactly(string? expected, string? supplied, bool matches)
    {
        Assert.Equal(matches, SparkConnectionString.PaymentKeyMatches(expected, supplied));
    }
}

public class SparkConnectionStringHandlerTests
{
    private const string ValidConnectionString = "type=flint;store-id=store-1;key=secret";

    [Fact]
    public void Constructing_the_handler_does_not_resolve_the_client_resolver()
    {
        // The invariant that keeps this class off BTCPay's container graph. BTCPay builds this handler from
        // inside PaymentMethodHandlerDictionary's own construction, and resolving SparkService there closes a
        // dependency cycle that hangs BTCPay's startup permanently (see the class remarks). Constructing the
        // handler must therefore touch nothing.
        var resolved = 0;

        var handler = new SparkConnectionStringHandler(() =>
        {
            resolved++;
            return new StubResolver();
        });

        Assert.Equal(0, resolved);

        // And a string that is not ours still does not need it.
        handler.Create("type=eclair;server=http://localhost:8080/", Network.RegTest, out _);
        Assert.Equal(0, resolved);

        handler.Create(ValidConnectionString, Network.RegTest, out _);
        Assert.Equal(1, resolved);
    }

    [Fact]
    public void Create_returns_null_without_error_for_another_provider()
    {
        var handler = new SparkConnectionStringHandler(() => new StubResolver());

        var client = handler.Create("type=eclair;server=http://localhost:8080/", Network.RegTest, out var error);

        Assert.Null(client);
        Assert.Null(error);
    }

    [Fact]
    public void Create_returns_an_error_for_our_type_with_missing_components()
    {
        var resolver = new StubResolver();
        var handler = new SparkConnectionStringHandler(() => resolver);

        var client = handler.Create("type=flint;store-id=store-1", Network.RegTest, out var error);

        Assert.Null(client);
        Assert.NotNull(error);
        // The resolver must not even be consulted for a malformed string.
        Assert.Empty(resolver.Calls);
    }

    [Fact]
    public void Create_passes_the_store_id_key_and_network_to_the_resolver()
    {
        var resolver = new StubResolver();
        var handler = new SparkConnectionStringHandler(() => resolver);

        handler.Create(ValidConnectionString, Network.RegTest, out _);

        var call = Assert.Single(resolver.Calls);
        Assert.Equal("store-1", call.StoreId);
        Assert.Equal("secret", call.PaymentKey);
        Assert.Same(Network.RegTest, call.Network);
    }

    [Fact]
    public void Create_surfaces_the_resolver_error()
    {
        var resolver = new StubResolver { Result = SparkClientResolution.Failed("nope") };
        var handler = new SparkConnectionStringHandler(() => resolver);

        var client = handler.Create(ValidConnectionString, Network.RegTest, out var error);

        Assert.Null(client);
        Assert.Equal("nope", error);
    }

    [Fact]
    public void Create_returns_the_resolved_client()
    {
        var expected = new StubLightningClient();
        var resolver = new StubResolver { Result = SparkClientResolution.Resolved(expected) };
        var handler = new SparkConnectionStringHandler(() => resolver);

        var client = handler.Create(ValidConnectionString, Network.RegTest, out var error);

        Assert.Same(expected, client);
        Assert.Null(error);
    }

    private sealed class StubResolver : ISparkClientResolver
    {
        public List<(string StoreId, string PaymentKey, Network Network)> Calls { get; } = [];

        public SparkClientResolution Result { get; set; } = SparkClientResolution.Failed("not configured");

        public SparkClientResolution Resolve(string storeId, string paymentKey, Network network)
        {
            Calls.Add((storeId, paymentKey, network));
            return Result;
        }
    }

    /// <summary>Identity-only stand-in; none of its members are called.</summary>
    private sealed class StubLightningClient : ILightningClient
    {
        public Task<LightningInvoice> GetInvoice(string invoiceId, CancellationToken cancellation = default) =>
            throw new NotSupportedException();

        public Task<LightningInvoice> GetInvoice(uint256 paymentHash, CancellationToken cancellation = default) =>
            throw new NotSupportedException();

        public Task<LightningInvoice[]> ListInvoices(CancellationToken cancellation = default) =>
            throw new NotSupportedException();

        public Task<LightningInvoice[]> ListInvoices(ListInvoicesParams request,
            CancellationToken cancellation = default) => throw new NotSupportedException();

        public Task<LightningPayment> GetPayment(string paymentHash, CancellationToken cancellation = default) =>
            throw new NotSupportedException();

        public Task<LightningPayment[]> ListPayments(CancellationToken cancellation = default) =>
            throw new NotSupportedException();

        public Task<LightningPayment[]> ListPayments(ListPaymentsParams request,
            CancellationToken cancellation = default) => throw new NotSupportedException();

        public Task<LightningInvoice> CreateInvoice(LightMoney amount, string description, TimeSpan expiry,
            CancellationToken cancellation = default) => throw new NotSupportedException();

        public Task<LightningInvoice> CreateInvoice(CreateInvoiceParams createInvoiceRequest,
            CancellationToken cancellation = default) => throw new NotSupportedException();

        public Task<ILightningInvoiceListener> Listen(CancellationToken cancellation = default) =>
            throw new NotSupportedException();

        public Task<LightningNodeInformation> GetInfo(CancellationToken cancellation = default) =>
            throw new NotSupportedException();

        public Task<LightningNodeBalance> GetBalance(CancellationToken cancellation = default) =>
            throw new NotSupportedException();

        public Task<PayResponse> Pay(PayInvoiceParams payParams, CancellationToken cancellation = default) =>
            throw new NotSupportedException();

        public Task<PayResponse> Pay(string bolt11, PayInvoiceParams payParams,
            CancellationToken cancellation = default) => throw new NotSupportedException();

        public Task<PayResponse> Pay(string bolt11, CancellationToken cancellation = default) =>
            throw new NotSupportedException();

        public Task<OpenChannelResponse> OpenChannel(OpenChannelRequest openChannelRequest,
            CancellationToken cancellation = default) => throw new NotSupportedException();

        public Task<BitcoinAddress> GetDepositAddress(CancellationToken cancellation = default) =>
            throw new NotSupportedException();

        public Task<ConnectionResult> ConnectTo(NodeInfo nodeInfo, CancellationToken cancellation = default) =>
            throw new NotSupportedException();

        public Task CancelInvoice(string invoiceId, CancellationToken cancellation = default) =>
            throw new NotSupportedException();

        public Task<LightningChannel[]> ListChannels(CancellationToken cancellation = default) =>
            throw new NotSupportedException();
    }
}
