using System;

namespace BTCPayServer.Plugins.Flint.Sdk;

/// <summary>
/// Wraps a failure that happened strictly <em>before</em> <c>SendPayment</c> was called, so the caller can
/// report "definitely not sent" instead of "unknown".
/// </summary>
/// <remarks>
/// <para>
/// Audit finding PaymentFlow F2. <c>SendBolt11Async</c> does a <c>PrepareSendPayment</c> round trip before it
/// sends anything, and a transient SSP or network failure there is indistinguishable, from outside, from a
/// timeout on the send itself — both arrive at the caller as a bare exception. The caller has to assume the
/// worst, so it returned <c>PayResult.Unknown</c>; BTCPay then marks the payout <c>InProgress</c> and
/// <c>LightningPendingPayoutListener</c> later resolves it through <c>GetPayment</c>, finds nothing, and
/// <b>cancels the payout</b> — ten minutes after a blip that moved no money. Under sustained SSP flakiness
/// that repeats, and each cancellation costs the claimant a fresh claim.
/// </para>
/// <para>
/// Naming the pre-send phase removes the ambiguity at the only place that can tell the difference: inside the
/// SDK client, which knows whether it had reached <c>SendPayment</c> yet. Everything this wraps provably spent
/// nothing, so the caller maps it to <c>Error</c> and BTCPay returns the payout to <c>AwaitingPayment</c> for
/// an immediate, safe retry.
/// </para>
/// <para>
/// Deliberately narrow: it wraps the prepare call and nothing else. Widening it to cover the send would
/// reintroduce exactly the bug it exists to fix, so a failure at or after <c>SendPayment</c> must keep
/// escaping as its original exception.
/// </para>
/// </remarks>
public sealed class SparkPreSendException : Exception
{
    public SparkPreSendException(Exception innerException)
        : base("The payment failed before it was sent.", innerException)
    {
    }
}
