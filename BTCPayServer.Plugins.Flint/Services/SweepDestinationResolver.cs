using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NBitcoin;

namespace BTCPayServer.Plugins.Flint.Services;

/// <summary>
/// What kind of thing a sweep is being sent to.
/// </summary>
/// <remarks>
/// One member today, and that is the point: it is the seam Phase 2 widens. A stablecoin balance or an EVM
/// address arrives as another member here plus another branch in the engine's send step, leaving the engine's
/// sequence — resolve in flight, sync, threshold, guard, persist, send — untouched.
/// </remarks>
public enum SweepDestinationKind
{
    /// <summary>A Bitcoin address, reached by cooperative exit.</summary>
    BitcoinAddress,

    /// <summary>
    /// An address on an EVM chain, reached by the SDK's cross-chain send through a bridge provider.
    /// </summary>
    /// <remarks>
    /// A different rail with different arithmetic, not a different address format. Its amount may be
    /// denominated in a token rather than in satoshi, its quote overpays the source leg, and — when the source
    /// is a token balance — it cannot carry an idempotency key. The engine branches on this at the send step
    /// and nowhere else; every guard before it is shared.
    /// </remarks>
    EvmAddress
}

/// <summary>Where one sweep is going.</summary>
/// <param name="Address">The resolved destination.</param>
/// <param name="Mode">Which configured rule produced it.</param>
/// <param name="Kind">What rail it will be sent on.</param>
/// <param name="Rotates">
/// True when this destination is fresh for every sweep, so the UI can say so honestly rather than showing one
/// address as though it were permanent.
/// </param>
public sealed record SweepDestination(
    string Address,
    SweepDestinationMode Mode,
    SweepDestinationKind Kind,
    bool Rotates);

/// <summary>
/// A resolved destination, or the reason there is not one. Exactly one of the two is set.
/// </summary>
public sealed record SweepDestinationResolution(SweepDestination? Destination, string? RefusalReason)
{
    public static SweepDestinationResolution Resolved(SweepDestination destination) => new(destination, null);

    public static SweepDestinationResolution Refused(string reason) => new(null, reason);
}

/// <summary>
/// Decides where a store's sweep goes, and refuses rather than improvising when it cannot tell.
/// </summary>
/// <remarks>
/// <para>
/// Split from <see cref="ISweepAddressSource"/> so the rules — which mode wins, what a missing wallet means,
/// whether a static address is usable on this chain — are unit-testable, while the part that talks to NBXplorer
/// stays thin. Same division as <c>SparkLightningWiring</c> and <c>BTCPayStoreLightningConfigStore</c>.
/// </para>
/// <para>
/// <b>The refusals are the feature.</b> A store configured for <see cref="SweepDestinationMode.StoreWallet"/>
/// with no on-chain wallet must be told so, not quietly fall back to whatever <c>StaticAddress</c> holds from an
/// earlier configuration — that would send a merchant's balance to an address they had stopped intending to use.
/// Every path either returns an address the merchant configured or a reason they can act on.
/// </para>
/// </remarks>
public sealed class SweepDestinationResolver
{
    private readonly ISweepAddressSource _addressSource;
    private readonly Network _network;
    private readonly ILogger<SweepDestinationResolver> _logger;

    /// <param name="network">
    /// The chain this server runs on, used to reject an address for a different one. Null when the chain is not
    /// one Spark supports, in which case no sweep can be resolved at all — <c>SparkService</c> refuses to start a
    /// wallet on such a server, so this is belt and braces rather than a reachable state.
    /// </param>
    public SweepDestinationResolver(
        ISweepAddressSource addressSource,
        Network? network,
        ILogger<SweepDestinationResolver> logger)
    {
        _addressSource = addressSource;
        _network = network ?? Network.Main;
        _logger = logger;
    }

    /// <summary>The chain destinations are validated against.</summary>
    public Network Network => _network;

    /// <param name="reserve">
    /// True for a real sweep: the store-wallet address is reserved and labelled, and therefore rotated. False to
    /// resolve a destination for a fee quote without consuming an address.
    /// </param>
    public async Task<SweepDestinationResolution> ResolveAsync(
        string storeId,
        SweepSettings settings,
        bool reserve,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(storeId);
        ArgumentNullException.ThrowIfNull(settings);

        switch (settings.DestinationMode)
        {
            case SweepDestinationMode.StaticAddress:
                return ResolveStatic(settings.StaticAddress);

            case SweepDestinationMode.EvmAddress:
                return ResolveEvm(settings);

            case SweepDestinationMode.StoreWallet:
                var result = await _addressSource
                    .GetAddressAsync(storeId, reserve, cancellationToken)
                    .ConfigureAwait(false);

                if (result.Status is not SweepAddressStatus.Available || result.Address is null)
                {
                    _logger.LogWarning(
                        "Store {StoreId}: no sweep destination from its Bitcoin wallet ({Status}): {Reason}",
                        storeId, result.Status, result.Reason);
                    return SweepDestinationResolution.Refused(
                        result.Reason ?? "This store's Bitcoin wallet did not provide a sweep address.");
                }

                // Validated even though BTCPay produced it. It costs nothing, and it is the difference between
                // a clear refusal here and an "Invalid network" surfacing from the SDK as a generic error after
                // a sweep record has already been written.
                if (!TryParse(result.Address, _network, out var parseError))
                {
                    _logger.LogError(
                        "Store {StoreId}: its Bitcoin wallet returned an address this server cannot use on "
                        + "{Network}: {Reason}", storeId, _network.ChainName, parseError);
                    return SweepDestinationResolution.Refused(
                        $"This store's Bitcoin wallet returned an address that is not valid on "
                        + $"{_network.ChainName}: {parseError}");
                }

                return SweepDestinationResolution.Resolved(new SweepDestination(
                    result.Address,
                    SweepDestinationMode.StoreWallet,
                    SweepDestinationKind.BitcoinAddress,
                    Rotates: true));

            default:
                return SweepDestinationResolution.Refused(
                    "This store's sweep destination is not configured. Open the sweep settings and choose one.");
        }
    }

    private SweepDestinationResolution ResolveStatic(string? address)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            return SweepDestinationResolution.Refused(
                "Sweeps are set to a fixed address, but no address has been entered.");
        }

        var trimmed = address.Trim();
        if (!TryParse(trimmed, _network, out var error))
        {
            // Re-validated at send time rather than trusted from the save that accepted it: settings can be
            // written by a future API path, restored from a backup taken on another chain, or edited by hand in
            // the database, and none of those went through the form's validation.
            return SweepDestinationResolution.Refused(
                $"The fixed sweep address is not usable on {_network.ChainName}: {error}");
        }

        return SweepDestinationResolution.Resolved(new SweepDestination(
            trimmed,
            SweepDestinationMode.StaticAddress,
            SweepDestinationKind.BitcoinAddress,
            Rotates: false));
    }

    private SweepDestinationResolution ResolveEvm(SweepSettings settings)
    {
        if (!TryParseEvm(settings.EvmAddress, out var normalised, out var error))
        {
            // Re-validated at send time rather than trusted from the save, for the same reason a static Bitcoin
            // address is: a settings blob can arrive from a backup, an API call or a hand edit.
            return SweepDestinationResolution.Refused(
                $"The cross-chain sweep address is not usable: {error}");
        }

        return SweepDestinationResolution.Resolved(new SweepDestination(
            normalised!,
            SweepDestinationMode.EvmAddress,
            SweepDestinationKind.EvmAddress,
            // One address the merchant chose, deliberately not rotated. Unlike a Bitcoin destination there is
            // nothing to rotate from — the plugin does not own an EVM key tree — so claiming rotation would be
            // a privacy promise it cannot keep.
            Rotates: false));
    }

    /// <summary>
    /// Whether a string is a well-formed EVM address, and its checksum-neutral normal form.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Static so the settings form applies the identical check, exactly as with
    /// <see cref="TryParse"/>. It is a <b>structural</b> check only: 20 bytes, hex, <c>0x</c>-prefixed. That is
    /// as much as can be decided locally, and it is worth deciding locally because it is the difference between
    /// telling a merchant their address is malformed while they are typing it and telling them after a sweep
    /// pass has failed.
    /// </para>
    /// <para>
    /// <b>The EIP-55 checksum is verified when the address carries one.</b> A mixed-case EVM address is
    /// checksummed: the capitalisation of each hex letter encodes bits of the address's own hash, so two
    /// transposed digits change the expected casing and are caught. An address that is entirely lower-case or
    /// entirely upper-case carries no checksum at all and is accepted as-is, because plenty of legitimate
    /// addresses are handed around that way and refusing them would refuse a destination that works.
    /// </para>
    /// <para>
    /// This is worth doing here rather than leaving to the SDK, and the reason is that <b>delivery is
    /// irreversible</b>. The SDK's parser decides the address <em>family</em>; it does not tell you the merchant
    /// typed the address they meant. Two transposed digits in a checksummed address pass every structural check
    /// and send a merchant's USDT to nobody, permanently.
    /// </para>
    /// <para>
    /// Note what this cannot tell you: an EVM address carries no chain. The SDK's own parser returns a null
    /// <c>chainId</c> for a bare address, which is why the destination chain is a separate setting rather than
    /// something inferred — and why sending to the right address on the wrong chain is a mistake no validation
    /// can catch.
    /// </para>
    /// </remarks>
    public static bool TryParseEvm(string? address, out string? normalised, out string? error)
    {
        normalised = null;

        if (string.IsNullOrWhiteSpace(address))
        {
            error = "no address was supplied";
            return false;
        }

        var candidate = address.Trim();

        if (!candidate.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            error = "an EVM address starts with 0x";
            return false;
        }

        if (candidate.Length != 42)
        {
            error = $"an EVM address is 42 characters including the 0x, and this one is {candidate.Length}";
            return false;
        }

        for (var index = 2; index < candidate.Length; index++)
        {
            if (!Uri.IsHexDigit(candidate[index]))
            {
                error = "an EVM address is hexadecimal after the 0x";
                return false;
            }
        }

        var body = candidate[2..];
        var hasLower = body.Any(char.IsLower);
        var hasUpper = body.Any(char.IsUpper);

        // Mixed case means the address is EIP-55 checksummed, so the casing is data and can be verified. A
        // uniformly-cased address carries no checksum and there is nothing to check.
        if (hasLower && hasUpper && !MatchesEip55Checksum(body))
        {
            error = "its checksum does not match, so at least one character is wrong. Copy it again from the "
                    + "wallet that owns it";
            return false;
        }

        // Case is preserved rather than lower-cased. It is the merchant's own typo protection, and it is worth
        // keeping visible in the settings page and in the sweep history.
        normalised = candidate;
        error = null;
        return true;
    }

    /// <summary>
    /// EIP-55: a hex letter is upper-case exactly when the corresponding nibble of keccak-256 of the
    /// lower-cased address is 8 or above.
    /// </summary>
    /// <remarks>
    /// Keccak-256 is <b>not</b> SHA3-256 — the padding differs — and .NET ships neither, so the permutation is
    /// implemented here. It is about forty lines and completely specified, which is a far better trade than
    /// taking a dependency for one hash, or than shipping no checksum check on an irreversible destination.
    /// </remarks>
    private static bool MatchesEip55Checksum(string body)
    {
        var lower = body.ToLowerInvariant();
        var hash = Keccak256(System.Text.Encoding.ASCII.GetBytes(lower));

        for (var i = 0; i < lower.Length; i++)
        {
            if (!char.IsLetter(lower[i]))
                continue;

            // The i-th nibble of the hash: high nibble for even i, low for odd.
            var nibble = (i & 1) == 0 ? hash[i / 2] >> 4 : hash[i / 2] & 0x0F;
            var shouldBeUpper = nibble >= 8;

            if (char.IsUpper(body[i]) != shouldBeUpper)
                return false;
        }

        return true;
    }

    /// <summary>Keccak-256 (the pre-standardisation padding), as Ethereum uses it.</summary>
    private static byte[] Keccak256(byte[] input)
    {
        const int rate = 136; // 1088 bits, the rate for a 256-bit digest.
        var state = new ulong[25];

        var padded = new byte[((input.Length / rate) + 1) * rate];
        input.CopyTo(padded, 0);
        // Keccak's original padding: 0x01, not SHA3's 0x06. This one byte is the whole difference.
        padded[input.Length] = 0x01;
        padded[^1] |= 0x80;

        for (var offset = 0; offset < padded.Length; offset += rate)
        {
            for (var i = 0; i < rate / 8; i++)
                state[i] ^= BitConverter.ToUInt64(padded, offset + (i * 8));
            KeccakF(state);
        }

        var digest = new byte[32];
        for (var i = 0; i < 4; i++)
            BitConverter.GetBytes(state[i]).CopyTo(digest, i * 8);
        return digest;
    }

    private static void KeccakF(ulong[] a)
    {
        var b = new ulong[25];
        var c = new ulong[5];
        var d = new ulong[5];

        for (var round = 0; round < 24; round++)
        {
            for (var x = 0; x < 5; x++)
                c[x] = a[x] ^ a[x + 5] ^ a[x + 10] ^ a[x + 15] ^ a[x + 20];

            for (var x = 0; x < 5; x++)
                d[x] = c[(x + 4) % 5] ^ System.Numerics.BitOperations.RotateLeft(c[(x + 1) % 5], 1);

            for (var x = 0; x < 5; x++)
            {
                for (var y = 0; y < 5; y++)
                    a[x + (5 * y)] ^= d[x];
            }

            for (var x = 0; x < 5; x++)
            {
                for (var y = 0; y < 5; y++)
                {
                    b[y + (5 * (((2 * x) + (3 * y)) % 5))] =
                        System.Numerics.BitOperations.RotateLeft(a[x + (5 * y)], KeccakRotations[x, y]);
                }
            }

            for (var x = 0; x < 5; x++)
            {
                for (var y = 0; y < 5; y++)
                    a[x + (5 * y)] = b[x + (5 * y)] ^ (~b[((x + 1) % 5) + (5 * y)] & b[((x + 2) % 5) + (5 * y)]);
            }

            a[0] ^= KeccakRoundConstants[round];
        }
    }

    private static readonly int[,] KeccakRotations =
    {
        { 0, 36, 3, 41, 18 },
        { 1, 44, 10, 45, 2 },
        { 62, 6, 43, 15, 61 },
        { 28, 55, 25, 21, 56 },
        { 27, 20, 39, 8, 14 }
    };

    private static readonly ulong[] KeccakRoundConstants =
    {
        0x0000000000000001UL, 0x0000000000008082UL, 0x800000000000808AUL, 0x8000000080008000UL,
        0x000000000000808BUL, 0x0000000080000001UL, 0x8000000080008081UL, 0x8000000000008009UL,
        0x000000000000008AUL, 0x0000000000000088UL, 0x0000000080008009UL, 0x000000008000000AUL,
        0x000000008000808BUL, 0x800000000000008BUL, 0x8000000000008089UL, 0x8000000000008003UL,
        0x8000000000008002UL, 0x8000000000000080UL, 0x000000000000800AUL, 0x800000008000000AUL,
        0x8000000080008081UL, 0x8000000000008080UL, 0x0000000080000001UL, 0x8000000080008008UL
    };

    /// <summary>
    /// Whether an address is a Bitcoin address this server could pay on its own chain.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Static so the settings form can use the identical check, and so "valid on save" and "valid at send" cannot
    /// drift apart.
    /// </para>
    /// <para>
    /// A bare address, deliberately — not a BIP21 URI. The SDK rejects <c>bitcoin:…?amount=…</c> with
    /// "Unsupported payment method", and silently stripping the URI would accept a string whose <c>amount</c> and
    /// <c>label</c> parameters the merchant may believe are being honoured.
    /// </para>
    /// </remarks>
    public static bool TryParse(string? address, Network network, out string? error)
    {
        ArgumentNullException.ThrowIfNull(network);

        if (string.IsNullOrWhiteSpace(address))
        {
            error = "no address was supplied";
            return false;
        }

        var candidate = address.Trim();
        if (candidate.Contains(':', StringComparison.Ordinal))
        {
            error = "enter a plain address, not a bitcoin: payment link";
            return false;
        }

        try
        {
            BitcoinAddress.Create(candidate, network);
            error = null;
            return true;
        }
        catch (Exception)
        {
            // NBitcoin's own text ("Invalid base58 data", "Invalid Bech32 string") names an encoding the merchant
            // did not choose and does not distinguish a typo from a mainnet address on a regtest server, which is
            // the mistake actually worth naming.
            error = $"it is not a valid Bitcoin address for {network.ChainName}";
            return false;
        }
    }
}
