using BTCPayServer.Plugins.Flint.Sdk;

namespace BTCPayServer.Plugins.Flint.Tests.Fakes;

/// <summary>
/// <see cref="IBolt11Parser"/> keyed on the invoice string.
/// </summary>
/// <remarks>
/// A stub rather than the real NBitcoin parser because producing a valid, signed BOLT11 for an arbitrary
/// payment hash and amount is a test fixture in its own right. What matters for the logic under test is
/// which field the hash came from and what happens when parsing fails, both of which this controls
/// directly. The real parser is a thin wrapper over NBitcoin and is exercised on regtest instead.
/// </remarks>
public sealed class StubBolt11Parser : IBolt11Parser
{
    private readonly Dictionary<string, Bolt11Info?> _known = [];

    public List<string> Calls { get; } = [];

    /// <summary>Returned for any invoice not explicitly registered. Null means "unparseable".</summary>
    public Bolt11Info? Fallback { get; set; }

    public StubBolt11Parser Register(string bolt11, Bolt11Info? info)
    {
        _known[bolt11] = info;
        return this;
    }

    public Bolt11Info? Parse(string bolt11)
    {
        Calls.Add(bolt11);
        return _known.TryGetValue(bolt11, out var info) ? info : Fallback;
    }
}
