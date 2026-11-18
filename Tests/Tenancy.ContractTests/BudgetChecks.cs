using Cehpalon.Engine.Core;
using Cehpalon.Engine.Tenancy;

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

    internal static async Task TenantLimits()
    {
        var budget = new TenantOperationConcurrencyBudget(2, 1);
        var a = HeldFixture();
        var b = HeldFixture();
        var fa = a.Build(tenantBudget: budget);
        var fb = b.Build(tenantBudget: budget);
        var outsider = new Fixture();
        var noMembershipB = outsider.Build(tenantBudget: budget);
        var pendingA = TenantCall(fa, "A").AsTask();
        Task<OperationResult<int>>? pendingB = null;
        try
        {
            await a.CleanupEntered!.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Verify((await TenantCall(fb, "A")).Error.Code == OperationConcurrencyBudget.CapacityExceededCode);
            Verify(b.Created == 0 && a.Reads == 1 && a.Disposed == 0);

            pendingB = TenantCall(fb, "B").AsTask();
            await b.CleanupEntered!.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Verify((await TenantCall(noMembershipB, "B")).Error.Code == "access.denied");
            Verify((await TenantCall(noMembershipB, "C")).Error.Code == OperationConcurrencyBudget.CapacityExceededCode);
            var rejected = await Task.WhenAll(Enumerable.Range(0, 32).Select(i =>
                Task.Run(async () => await TenantCall(i % 2 == 0 ? fa : fb, i % 2 == 0 ? "B" : "A"))));
            Verify(rejected.All(x => x.Error.Code == OperationConcurrencyBudget.CapacityExceededCode));
            Verify(a.Created == 1 && b.Created == 1 && outsider.Created == 0);

            a.CleanupRelease!.SetResult();
            Verify((await pendingA).IsSuccess && a.Disposed == 1);
            Verify((await TenantCall(noMembershipB, "C")).IsSuccess && outsider.Disposed == 1);
            Verify((await TenantCall(fa, "B")).Error.Code == OperationConcurrencyBudget.CapacityExceededCode);
        }
        finally
        {
            a.CleanupRelease!.TrySetResult();
            b.CleanupRelease!.TrySetResult();
        }
        Verify((await pendingB!).IsSuccess && b.Disposed == 1);
        Verify((await TenantCall(fa, "B")).IsSuccess && a.Disposed == 2);
    }

    internal static async Task TenantFailureRecovery()
    {
        foreach (var (total, tenant) in new[] { (0, 1), (1, 0), (1, 2) })
        {
            try { _ = new TenantOperationConcurrencyBudget(total, tenant); throw new InvalidOperationException("Expected invalid limits"); }
            catch (ArgumentOutOfRangeException) { }
        }

        var budget = new TenantOperationConcurrencyBudget(1, 1);
        var fixture = new Fixture();
        var dispatcher = fixture.Build(tenantBudget: budget);
        using var cancel = new CancellationTokenSource();
        fixture.Hooks["factory"] = cancel.Cancel;
        await Throws<OperationCanceledException>(() => TenantCall(dispatcher, "A", cancel.Token));
        Verify(fixture.Created == 1 && fixture.Disposed == 1 && fixture.Reads == 0);
        fixture.Hooks.Clear();

        fixture.CleanupFailure = true;
        await Throws<InvalidDataException>(() => TenantCall(dispatcher, "A"));
        fixture.CleanupFailure = false;
        Verify((await TenantCall(dispatcher, "A")).IsSuccess);
        Verify(fixture.Created == 3 && fixture.Disposed == 3);
    }

    private static Fixture HeldFixture() => new()
    {
        MemberB = true,
        CleanupEntered = new(TaskCreationOptions.RunContinuationsAsynchronously),
        CleanupRelease = new(TaskCreationOptions.RunContinuationsAsynchronously)
    };

    private static ValueTask<OperationResult<int>> TenantCall(
        TenantOperationDispatcher dispatcher, string tenant, CancellationToken token = default) =>
        dispatcher.DispatchAsync<Request, int>("document.read", new(tenant, "shared"), Context, token);

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
