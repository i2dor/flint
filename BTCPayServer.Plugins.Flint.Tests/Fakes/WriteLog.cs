namespace BTCPayServer.Plugins.Flint.Tests.Fakes;

/// <summary>
/// A single ordered record of writes across several fakes.
/// </summary>
/// <remarks>
/// Exists because per-fake call counters cannot express ordering, and ordering is the invariant that matters
/// most in provisioning: the settings must be stored — which is what starts the wallet — before the store's
/// Lightning payment method points at it, so a store never advertises a Lightning wallet that failed to start.
/// Two independent <c>Assert.Single</c> checks pass just as happily with the two writes reversed.
/// </remarks>
public sealed class WriteLog
{
    public List<string> Entries { get; } = [];

    public void Record(string entry) => Entries.Add(entry);
}
