using System;
using System.Threading;
using System.Threading.Tasks;

namespace BTCPayServer.Plugins.Flint.Sdk;

/// <summary>
/// Bounds how long a background loop waits on an SDK call.
/// </summary>
/// <remarks>
/// <para>
/// No <c>IBreezSdk</c> method takes a <see cref="CancellationToken"/> and none can be cancelled, so this
/// abandons the <em>wait</em> and never the call. That is still worth doing: a hung service-provider request
/// would otherwise stall the loop awaiting it — a store's whole event queue, or a reconciliation pass — behind
/// one request that may never come back.
/// </para>
/// <para>
/// Two details that are easy to get wrong and both matter at this call frequency. The timer backing the
/// deadline is cancelled the moment the real task wins, because otherwise every call leaves a live timer behind
/// until it fires. And an abandoned task's exception is observed, because otherwise a later fault surfaces as
/// an unobserved task exception with no context, long after the call site is gone.
/// </para>
/// </remarks>
internal static class SparkDeadline
{
    /// <summary>
    /// Awaits <paramref name="task"/> for at most <paramref name="deadline"/>, returning null on timeout.
    /// </summary>
    /// <param name="onTimeout">Invoked on timeout so the caller can log with its own context.</param>
    /// <remarks>
    /// <typeparamref name="TResult"/> is not declared nullable on the parameter so that both a
    /// <c>Task&lt;T&gt;</c> and a <c>Task&lt;T?&gt;</c> bind without a cast at the call site; a null return
    /// always means the deadline passed, which is exactly what callers need to distinguish.
    /// </remarks>
    internal static async Task<TResult?> OrNullAsync<TResult>(
        Task<TResult> task,
        TimeSpan deadline,
        Action onTimeout,
        CancellationToken cancellationToken)
        where TResult : class?
    {
        ArgumentNullException.ThrowIfNull(task);

        if (!await CompletedWithinAsync(task, deadline, onTimeout, cancellationToken).ConfigureAwait(false))
            return null;

        return await task.ConfigureAwait(false);
    }

    /// <summary>
    /// Awaits <paramref name="task"/> for at most <paramref name="deadline"/>, returning false on timeout.
    /// </summary>
    /// <remarks>
    /// The non-generic sibling of <see cref="OrNullAsync{TResult}"/>, for a unit-returning call whose only
    /// interesting answer is whether it finished. It propagates the task's exception when the task won, so a
    /// caller's own error handling still sees it; an abandoned task's exception is observed instead, because by
    /// then there is nobody left to hand it to.
    /// </remarks>
    internal static async Task<bool> OrTimeoutAsync(
        Task task,
        TimeSpan deadline,
        Action onTimeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(task);

        if (!await CompletedWithinAsync(task, deadline, onTimeout, cancellationToken).ConfigureAwait(false))
            return false;

        await task.ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Waits for <paramref name="task"/> to finish within <paramref name="deadline"/>, without observing its
    /// result. Returns false when the deadline passed first.
    /// </summary>
    private static async Task<bool> CompletedWithinAsync(
        Task task,
        TimeSpan deadline,
        Action onTimeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(onTimeout);

        if (task.IsCompleted)
            return true;

        using var timerCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var timer = Task.Delay(deadline, timerCts.Token);
        var winner = await Task.WhenAny(task, timer).ConfigureAwait(false);

        if (winner == task)
        {
            // Releases the timer immediately. The resulting cancelled Task.Delay is cancelled rather than
            // faulted, so it needs no observation of its own.
            await timerCts.CancelAsync().ConfigureAwait(false);
            return true;
        }

        Observe(task);
        onTimeout();
        return false;
    }

    /// <summary>
    /// Attaches a no-op fault handler so an abandoned task cannot raise an unobserved task exception.
    /// </summary>
    private static void Observe(Task task) =>
        _ = task.ContinueWith(
            static abandoned => _ = abandoned.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
}
