using System;
using BTCPayServer.Plugins.Flint;
using Xunit;

namespace BTCPayServer.Plugins.Flint.Tests;

/// <summary>
/// The production pass budgets, checked against the intervals they are derived from.
/// </summary>
/// <remarks>
/// <para>
/// <c>SparkStorePassSchedulerTests</c> proves the budget <em>mechanism</em>: given a budget, a pass stops
/// starting new stores when it is spent, and the next pass resumes at the store it stopped on. Those tests
/// inject their own budgets, which is the right way to test a mechanism — and it means they say nothing at
/// all about the values that ship.
/// </para>
/// <para>
/// That gap is not hypothetical. Setting both production budgets to 365 days leaves all 1037 other tests
/// passing, so a budget that can never bind would ship green. This repository has shipped that shape before:
/// <c>EffectiveMinimumSweepSats</c> was defined, documented and enforced nowhere, and the deposit claim's
/// half-the-deposit backstop ran on the manual path only. A guard with no test on its live value is a guard
/// that is one careless edit from being decorative.
/// </para>
/// <para>
/// The invariant asserted here is the one the constants' own remarks state: a budget is half its task's
/// interval, so that a pass which spends its whole budget and then waits out one final store still costs
/// about one interval, capping that task's duty cycle on one of BTCPay's three shared scheduled-task workers
/// at roughly 50%.
/// </para>
/// </remarks>
public class PassBudgetConstantsTests
{
    public static TheoryData<string, TimeSpan, TimeSpan, TimeSpan> Budgets() => new()
    {
        {
            nameof(Constants.ReconciliationPassBudget),
            Constants.ReconciliationPassBudget,
            Constants.ReconciliationInterval,
            Constants.ReconciliationStoreDeadline
        },
        {
            nameof(Constants.SweepPassBudget),
            Constants.SweepPassBudget,
            Constants.SweepInterval,
            Constants.SweepStoreDeadline
        }
    };

    [Theory]
    [MemberData(nameof(Budgets))]
    public void A_pass_budget_is_half_its_interval(
        string name, TimeSpan budget, TimeSpan interval, TimeSpan storeDeadline)
    {
        Assert.True(
            budget > TimeSpan.Zero,
            $"{name} must be positive; a non-positive budget stops a pass before it starts any store.");

        Assert.True(
            budget <= interval / 2,
            $"{name} is {budget}, more than half its {interval} interval. A pass that overruns its own "
            + "interval keeps one of BTCPay's three shared scheduled-task workers busy more often than not.");

        // The worst case the remarks describe: the budget is spent, and one store is still running under the
        // per-store deadline when it is. That total is what actually has to fit inside an interval.
        Assert.True(
            budget + storeDeadline <= interval,
            $"{name} ({budget}) plus one {storeDeadline} store deadline exceeds the {interval} interval, so a "
            + "worst-case pass would still be running when the next one is due.");
    }
}
