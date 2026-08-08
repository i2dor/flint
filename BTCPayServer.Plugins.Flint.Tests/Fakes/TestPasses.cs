using BTCPayServer.Plugins.Flint.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace BTCPayServer.Plugins.Flint.Tests.Fakes;

/// <summary>
/// Store-pass schedulers for tests that need one but are not testing it.
/// </summary>
/// <remarks>
/// <b>Built from the production constants, deliberately.</b> A test-only "unbounded" scheduler would be a fake
/// more forgiving than the thing that ships — the failure mode this project has hit three times — and it would
/// mean that a budget small enough to break reconciliation could never be noticed by the suite. The budgets are
/// tens of seconds and the tests here take milliseconds, so nothing is flaky as a result; what is being borrowed
/// is the shipped configuration, not a convenient one.
/// </remarks>
public static class TestPasses
{
    public static SparkStorePassScheduler Reconciliation() => new(
        "reconciliation",
        Constants.ReconciliationPassBudget,
        Constants.ReconciliationStoreDeadline,
        TimeProvider.System,
        NullLogger.Instance);
}
