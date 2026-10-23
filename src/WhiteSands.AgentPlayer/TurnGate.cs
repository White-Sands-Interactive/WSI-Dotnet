using WhiteSands.AgentPlayer.Protocol;

namespace WhiteSands.AgentPlayer;

/// <summary>
/// Coordinates game-side <c>TurnAsync</c> with the agent's wait tool.
/// <c>TurnAsync</c> releases wait, then resolves when the platform reports the turn finished.
/// </summary>
public sealed class TurnGate
{
    private readonly object _gate = new();
    private TaskCompletionSource<bool>? _waitSource;
    private Task? _waitTask;
    private TaskCompletionSource<TurnReport>? _turnSource;
    private Task<TurnReport>? _turnTask;
    private bool _released;

    /// <summary>Called by the wait tool. Blocks until <c>TurnAsync</c> releases this session.</summary>
    public Task WaitForTurnAsync()
    {
        Task task;
        lock (_gate)
        {
            if (_released)
            {
                _released = false;
                return Task.CompletedTask;
            }

            if (_waitTask == null)
            {
                _waitSource = NewBool();
                _waitTask = _waitSource.Task;
            }

            task = _waitTask;
        }

        return ClearAfterWait(task);
    }

    /// <summary>
    /// Unblock wait and wait for the platform's turn report.
    /// Throws on timeout or cancellation; those do not produce a <see cref="TurnReport"/>.
    /// </summary>
    public async Task<TurnReport> RunTurnAsync(TimeSpan? timeout, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        Task<TurnReport> turnTask;
        CancellationTokenSource? timeoutSource = null;
        lock (_gate)
        {
            if (_turnTask != null)
            {
                throw new InvalidOperationException("turn() already in progress for this session");
            }

            _turnSource = NewTurn();
            _turnTask = _turnSource.Task;
            turnTask = _turnTask;
            if (timeout != null)
            {
                timeoutSource = new CancellationTokenSource(timeout.Value);
            }
        }

        CancellationTokenRegistration abortRegistration = default;
        CancellationTokenRegistration timeoutRegistration = default;
        if (timeoutSource != null)
        {
            var milliseconds = (long)timeout!.Value.TotalMilliseconds;
            timeoutRegistration = timeoutSource.Token.Register(() =>
                RejectTurn(new TurnTimeoutException($"turn() timed out after {milliseconds}ms")));
        }

        if (cancellationToken.CanBeCanceled)
        {
            abortRegistration = cancellationToken.Register(() => RejectTurn(AbortError(cancellationToken)));
        }

        TaskCompletionSource<bool>? release;
        lock (_gate)
        {
            _released = true;
            release = _waitSource;
            _waitSource = null;
            _waitTask = null;
        }

        release?.TrySetResult(true);

        try
        {
            return await turnTask.ConfigureAwait(false);
        }
        finally
        {
            abortRegistration.Dispose();
            timeoutRegistration.Dispose();
            timeoutSource?.Dispose();
            lock (_gate)
            {
                if (_turnTask == turnTask)
                {
                    _turnTask = null;
                    _turnSource = null;
                }
            }
        }
    }

    /// <summary>Platform finished one action cycle (success or second failure).</summary>
    public void CompleteTurn(TurnReport outcome)
    {
        TaskCompletionSource<TurnReport>? source;
        lock (_gate)
        {
            source = _turnSource;
            if (source == null)
            {
                return;
            }

            _turnSource = null;
        }

        source.TrySetResult(outcome);
    }

    /// <summary>Fail an in-flight turn, for example when the connection cannot be resumed.</summary>
    public void FailTurn(Exception error) => RejectTurn(error);

    public void Reset()
    {
        TaskCompletionSource<bool>? wait;
        lock (_gate)
        {
            wait = _waitSource;
            _waitSource = null;
            _waitTask = null;
            _released = false;
        }

        wait?.TrySetResult(true);
        CompleteTurn(new TurnReport { Ok = false, Error = "session stopped" });
    }

    private async Task ClearAfterWait(Task task)
    {
        await task.ConfigureAwait(false);
        lock (_gate)
        {
            if (_waitTask == task)
            {
                _waitTask = null;
                _waitSource = null;
            }

            // runTurn already cleared the waiter. This completion consumes the release.
            if (_released && _waitTask == null)
            {
                _released = false;
            }
        }
    }

    private void RejectTurn(Exception error)
    {
        TaskCompletionSource<TurnReport>? source;
        lock (_gate)
        {
            source = _turnSource;
            if (source == null)
            {
                return;
            }

            _turnSource = null;
        }

        source.TrySetException(error);
    }

    private static Exception AbortError(CancellationToken cancellationToken)
    {
        return new OperationCanceledException("turn aborted", cancellationToken);
    }

    private static TaskCompletionSource<bool> NewBool() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static TaskCompletionSource<TurnReport> NewTurn() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
