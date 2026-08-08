using BTCPayServer.Plugins.Flint.Services;

namespace BTCPayServer.Plugins.Flint.Tests.Fakes;

/// <summary>
/// In-memory <see cref="ISweepAddressSource"/> standing in for a store's BTCPay wallet.
/// </summary>
/// <remarks>
/// Models the property that matters about the real one: a <em>reserving</em> read consumes an address, so the next
/// reserving read returns a different one, while a peek does not. Tests that assert rotation would otherwise pass
/// against a source that handed out one fixed address forever.
/// </remarks>
public sealed class FakeSweepAddressSource : ISweepAddressSource
{
    /// <summary>
    /// Valid regtest P2WPKH addresses, handed out in order by reserving reads.
    /// </summary>
    /// <remarks>
    /// Real bech32 with correct checksums, verified against <c>BitcoinAddress.Create(…, Network.RegTest)</c> —
    /// which matters because the resolver validates whatever the wallet hands it, so plausible-looking rubbish
    /// here would make every store-wallet test fail for the wrong reason. Derived as
    /// <c>sha256("spark-sweep-test-N")[..20]</c> as a witness program, so nobody holds their keys.
    /// </remarks>
    public static readonly string[] RegtestAddresses =
    [
        "bcrt1qtxwcjjvf4ny9wsw9emgnpazey2vde3xhnyqpw0",
        "bcrt1qmffrhpr50qhysmrkpcrulepcd3rjwlc9ggr27k",
        "bcrt1qt8hufshrz62z5vj4q40uqx6c6ytlujy5s03gwm"
    ];

    private readonly List<string> _addresses;
    private int _reserved;

    public FakeSweepAddressSource(params string[] addresses)
    {
        _addresses = addresses.Length > 0 ? [.. addresses] : [.. RegtestAddresses];
    }

    /// <summary>Returned by every call when set, in place of an address.</summary>
    public SweepAddressResult? Result { get; set; }

    /// <summary>Every call, so a test can prove a preview did not reserve and a sweep did.</summary>
    public List<(string StoreId, bool Reserve)> Calls { get; } = [];

    public int ReservedCount => _reserved;

    public Task<SweepAddressResult> GetAddressAsync(
        string storeId,
        bool reserve,
        CancellationToken cancellationToken = default)
    {
        Calls.Add((storeId, reserve));

        if (Result is { } configured)
            return Task.FromResult(configured);

        if (!reserve)
        {
            // A peek: the next unused address, unconsumed. The same value a subsequent reserve would return, which
            // is what the real one does too.
            return Task.FromResult(SweepAddressResult.Available(_addresses[_reserved % _addresses.Count]));
        }

        var address = _addresses[_reserved % _addresses.Count];
        _reserved++;
        return Task.FromResult(SweepAddressResult.Available(address));
    }
}
