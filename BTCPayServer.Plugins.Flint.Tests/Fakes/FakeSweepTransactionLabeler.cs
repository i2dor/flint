using BTCPayServer.Plugins.Flint.Data;
using BTCPayServer.Plugins.Flint.Services;

namespace BTCPayServer.Plugins.Flint.Tests.Fakes;

/// <summary>
/// Records what the engine asked to label, so tests can assert a sweep's transaction gets labelled in the
/// store's wallet — and that a pass that sweeps nothing asks for no label.
/// </summary>
/// <remarks>
/// The cross-chain gate lives in the production labeler (it is a fact about what appears in a Bitcoin
/// wallet), so this fake records <em>every</em> call, cross-chain deliveries included: a test asserting "no
/// label" is then asserting the engine never asked, which is the stronger claim. It also means no test through
/// this fake can show that a cross-chain delivery goes unlabelled; that is the production labeler's to prove,
/// and nothing tests it yet.
/// </remarks>
public sealed class FakeSweepTransactionLabeler : ISweepTransactionLabeler
{
    public List<(string StoreId, string TxId, SweepDestinationKind Kind)> Labeled { get; } = [];

    public Task LabelAsync(SweepRecord record, string txId, CancellationToken cancellationToken = default)
    {
        Labeled.Add((record.StoreId, txId, record.DestinationKind));
        return Task.CompletedTask;
    }
}
