namespace Cehpalon.Engine.Core;

/// <summary>An opt-in, cooperative deadline for one operation. Experimental (M0).</summary>
public sealed class OperationDeadline
{
    /// <summary>The reserved error code returned when an operation observes deadline cancellation.</summary>
    public const string DeadlineExceededCode = "operation.deadline_exceeded";

    private readonly TimeProvider timeProvider;

    public OperationDeadline(TimeSpan timeout, TimeProvider? timeProvider = null)
    {
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromHours(1))
            throw new ArgumentOutOfRangeException(nameof(timeout), "The timeout must be greater than zero and at most one hour.");

        Timeout = timeout;
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public TimeSpan Timeout { get; }

    /// <summary>Invoke the operation with a per-call token linked to caller cancellation and the deadline.</summary>
    /// <remarks>
    /// Always await the operation, even if it ignores cancellation. A returned result is not changed.
    /// For a cancellation exception, caller cancellation wins; otherwise only expiry with the supplied
    /// operation token is translated into a deadline failure.
    /// </remarks>
    public ValueTask<OperationResult<TResponse>> ExecuteAsync<TResponse>(
        Func<CancellationToken, ValueTask<OperationResult<TResponse>>> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        cancellationToken.ThrowIfCancellationRequested();
        return ExecuteCoreAsync(operation, cancellationToken);
    }

    /// <summary>Dispatch with a cooperative deadline, including owned handler cleanup.</summary>
    /// <remarks>
    /// When an inner dispatch observes deadline cancellation, its completion outcome is
    /// <see cref="OperationDispatchOutcome.Canceled"/> even though this wrapper returns a failure result.
    /// </remarks>
    public ValueTask<OperationResult<TResponse>> DispatchAsync<TRequest, TResponse>(
        OperationDispatcher dispatcher, string id, TRequest request, OperationContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        OperationId.RequireValid(id);
        ArgumentNullException.ThrowIfNull(context);
        return ExecuteAsync(token => dispatcher.DispatchAsync<TRequest, TResponse>(id, request, context, token), cancellationToken);
    }

    private async ValueTask<OperationResult<TResponse>> ExecuteCoreAsync<TResponse>(
        Func<CancellationToken, ValueTask<OperationResult<TResponse>>> operation, CancellationToken cancellationToken)
    {
        using var deadline = new CancellationTokenSource(Timeout, timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        var operationToken = linked.Token;
        try
        {
            return await operation(operationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The operation returned a null result.");
        }
        catch (OperationCanceledException error)
        {
            var deadlineCancellation = error.CancellationToken == operationToken && deadline.IsCancellationRequested;
            cancellationToken.ThrowIfCancellationRequested();
            if (deadlineCancellation)
                return OperationResult<TResponse>.Failure(new OperationError(DeadlineExceededCode, "The operation deadline expired."));
            throw;
        }
    }
}
