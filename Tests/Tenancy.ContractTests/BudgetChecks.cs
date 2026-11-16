using Cehpalon.Engine.Core;

internal sealed class HoldingOperation(Task cleanup) : IOperationHandler<int, int>, IAsyncDisposable
{
    public ValueTask<OperationResult<int>> HandleAsync(int request, OperationContext context, CancellationToken token)
        => new(OperationResult<int>.Success(request));

    public ValueTask DisposeAsync() => new(cleanup);
}

internal static class BudgetChecks
{
    private static readonly OperationContext Context = new("test-budget");

    internal static async Task ConcurrentCleanup()
    {
        var budget = new OperationConcurrencyBudget(2);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var created = 0;
        var registry = new OperationRegistryBuilder().Register<int, int>("capacity.hold", () =>
        {
            Interlocked.Increment(ref created);
            return new HoldingOperation(release.Task);
        }).Build();
        var dispatcher = new OperationDispatcher(registry);
        var otherDispatcher = new OperationDispatcher(registry);
        var drivers = Enumerable.Range(0, 32).Select(i => Task.Run(async () =>
        {
            await start.Task;
            return budget.DispatchAsync<int, int>(i % 2 == 0 ? dispatcher : otherDispatcher, "capacity.hold", i, Context);
        })).ToArray();
        start.SetResult();
        try
        {
            var calls = await Task.WhenAll(drivers).WaitAsync(TimeSpan.FromSeconds(5));
            Verify(created == 2 && calls.Count(x => !x.IsCompleted) == 2);
            using var ac = new CancellationTokenSource();
            ac.Cancel();
            await Throws<OperationCanceledException>(() => budget.DispatchAsync<int, int>(dispatcher, "capacity.hold", 50, Context, ac.Token));
            release.SetResult();
            var results = await Task.WhenAll(calls.Select(call => call.AsTask()));
            Verify(results.Count(row => row.IsSuccess) == 2
                   && results.Where(row => !row.IsSuccess).All(row => row.Error.Code == OperationConcurrencyBudget.CapacityExceededCode)
                   && created == 2);
            Verify((await budget.DispatchAsync<int, int>(otherDispatcher, "capacity.hold", 51, Context)).Value == 51);
        }
        finally { release.TrySetResult(); }
    }

    internal static async Task FailureRecovery()
    {
        try { _ = new OperationConcurrencyBudget(0); throw new InvalidOperationException("Expected invalid capacity"); }
        catch (ArgumentOutOfRangeException) { }

        using var cancel = new CancellationTokenSource();
        var dispatcher = new OperationDispatcher(new OperationRegistryBuilder()
            .Register<int, int>("capacity.factory", () => throw new IOException("factory"))
            .Register<int, int>("capacity.cleanup", () => new HoldingOperation(Task.FromException(new InvalidDataException("cleanup"))))
            .Register<int, int>("capacity.cancel", () => { cancel.Cancel(); return new HoldingOperation(Task.CompletedTask); })
            .Register<int, int>("capacity.ok", () => new HoldingOperation(Task.CompletedTask)).Build());
        var budget = new OperationConcurrencyBudget(1);
        await Throws<IOException>(() => budget.DispatchAsync<int, int>(dispatcher, "capacity.factory", 0, Context));
        await Throws<InvalidDataException>(() => budget.DispatchAsync<int, int>(dispatcher, "capacity.cleanup", 0, Context));
        await Throws<OperationCanceledException>(() => budget.DispatchAsync<int, int>(dispatcher, "capacity.cancel", 0, Context, cancel.Token));
        Verify((await budget.DispatchAsync<int, int>(dispatcher, "capacity.ok", 42, Context)).Value == 42);
    }

    private static async ValueTask Throws<T>(Func<ValueTask<OperationResult<int>>> action) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}");
    }

    private static void Verify(bool condition)
    {
        if (!condition)
            throw new InvalidOperationException("Unexpected dispatch budget result");
    }
}
