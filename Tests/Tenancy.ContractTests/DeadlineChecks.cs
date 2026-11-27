using System.Runtime.CompilerServices;
using Cehpalon.Engine.Core;
using Cehpalon.Engine.Identity;
using Cehpalon.Engine.Tenancy;

internal static class DeadlineChecks
{
    private static readonly TimeSpan Watchdog = TimeSpan.FromSeconds(10); // Hang guard, not a timing assertion.

    internal static async Task Run(Func<string, Func<Task>, Task> check)
    {
        await check("tenant-deadline-copies-dispatcher-without-changing-original-and-rejects-null", async () =>
        {
            var f = new Probe();
            var original = f.Build();
            var error = await Throws<ArgumentNullException>(() => Call(original.WithOperationDeadline(null!)));
            Require(error.ParamName == "deadline");
            var configured = original.WithOperationDeadline(f.Deadline);
            Require(!ReferenceEquals(original, configured));
            Require((await Call(original)).Value == 42 && f.Timer is null);
            Require((await Call(configured)).Value == 42 && f.Timer?.Disposed == true);
            var first = f.Timer;
            Require((await Call(original)).Value == 42 && ReferenceEquals(first, f.Timer));
            Require((await Call(configured)).Value == 42 && !ReferenceEquals(first, f.Timer));
            Require(f.Created == 4 && f.Disposed == 4);
        });

        await check("tenant-deadline-starts-after-authority-and-awaited-authorization-audit", async () =>
        {
            var f = new Probe();
            using var caller = new CancellationTokenSource();
            var checks = 0;
            f.Authority = token => { Require(token == caller.Token && f.Timer is null); checks++; };
            var writing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            f.Append = async (entry, token) =>
            {
                Require(token != caller.Token);
                if (entry.Stage != TenantAuditStage.Authorization) return;
                Require(checks == 4 && f.Timer is null);
                writing.SetResult();
                await release.Task.WaitAsync(token);
            };
            f.Factory = () => Require(f.Events.Single().Stage == TenantAuditStage.Authorization && f.Timer?.Disposed == false);
            f.Business = (_, token) =>
            {
                Require(token != caller.Token && token.CanBeCanceled && !token.IsCancellationRequested);
                return OperationResult<int>.Success(42);
            };
            var pending = Call(f.Build(audited: true).WithOperationDeadline(f.Deadline), caller.Token).AsTask();
            try
            {
                await writing.Task.WaitAsync(Watchdog);
                Require(f.Timer is null && f.Created == 0 && !pending.IsCompleted);
            }
            finally { release.TrySetResult(); }
            Require((await pending.WaitAsync(Watchdog)).Value == 42);
        });

        await check("tenant-deadline-denial-does-not-start-timer-or-create-handler", async () =>
        {
            var f = new Probe { Allow = false };
            var result = await Call(f.Build().WithOperationDeadline(f.Deadline));
            Require(result.Error is { Code: "access.denied", Message: "Access denied." });
            Require(f.Timer is null && f.Created == 0);
        });

        await check("tenant-deadline-preserves-handler-access-denial-even-after-expiry", async () =>
        {
            var f = new Probe();
            f.Business = (_, _) => { f.Expire(); throw new TenantAccessDeniedException(); };
            var result = await Call(f.Build().WithOperationDeadline(f.Deadline));
            Require(result.Error is { Code: "access.denied", Message: "Access denied." } && f.Disposed == 1);
        });

        await check("tenant-deadline-preserves-post-execution-observation-denial", async () =>
        {
            var f = new Probe();
            f.Cleanup = () => { f.Clock.AdvanceElapsed(TimeSpan.FromSeconds(2)); return ValueTask.CompletedTask; };
            var result = await Call(f.Build(observed: true).WithOperationDeadline(f.Deadline));
            Require(result.Error.Code == "access.denied" && f.Created == 1 && f.Disposed == 1 && f.Timer?.Disposed == true);
        });

        foreach (var perTenant in new[] { false, true })
            await check(perTenant ? "tenant-deadline-shares-and-releases-per-tenant-budget" : "tenant-deadline-shares-and-releases-shared-budget", async () =>
            {
                var budget = perTenant ? null : new OperationConcurrencyBudget(1);
                var tenantBudget = perTenant ? new TenantOperationConcurrencyBudget(2, 1) : null;
                var f = new Probe();
                var peer = new Probe();
                var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                f.Business = (_, token) => throw f.ObserveExpiry(token);
                f.Cleanup = () => { ready.SetResult(); return new(release.Task); };
                var pending = Call(f.Build(budget: budget, tenantBudget: tenantBudget).WithOperationDeadline(f.Deadline)).AsTask();
                var unconfigured = peer.Build(budget: budget, tenantBudget: tenantBudget);
                int Active() => budget?.GetSnapshot().ActiveOperations ?? tenantBudget!.GetSnapshot().Total.ActiveOperations;
                try
                {
                    await ready.Task.WaitAsync(Watchdog);
                    Require(!pending.IsCompleted && f.Timer?.Disposed == false && Active() == 1);
                    var rejected = await Call(unconfigured.WithOperationDeadline(peer.Deadline));
                    Require(ReferenceEquals(rejected, await Call(unconfigured)));
                    Require(rejected.Error.Code == OperationConcurrencyBudget.CapacityExceededCode && peer.Created == 0 && peer.Timer?.Disposed == true);
                }
                finally { release.TrySetResult(); }
                DeadlineFailure(await pending.WaitAsync(Watchdog));
                Require(f.Timer?.Disposed == true && Active() == 0 && f.Disposed == 1);
                Require(tenantBudget is null || tenantBudget.GetSnapshot().ActiveTenants == 0);
                Require((await Call(unconfigured)).Value == 42);
            });

        await check("tenant-deadline-caller-cancellation-wins-over-expiry", async () =>
        {
            using var caller = new CancellationTokenSource();
            var f = new Probe();
            f.Business = (_, token) => { var error = f.ObserveExpiry(token); caller.Cancel(); throw error; };
            var error = await Throws<OperationCanceledException>(() => Call(f.Build(audited: true).WithOperationDeadline(f.Deadline), caller.Token));
            Require(error.CancellationToken == caller.Token && f.Disposed == 1);
            Outcomes(f, TenantAuditOutcome.Authorized, TenantAuditOutcome.Cancelled);
        });

        await check("tenant-deadline-does-not-replace-token-ignoring-results", async () =>
        {
            foreach (var expected in new[] { OperationResult<int>.Success(42), OperationResult<int>.Failure(new("business.invalid", "Business rejected.")) })
            {
                var f = new Probe();
                f.Business = (_, token) => { f.Expire(); Require(token.IsCancellationRequested); return expected; };
                Require(ReferenceEquals(await Call(f.Build().WithOperationDeadline(f.Deadline)), expected) && f.Disposed == 1);
            }
        });

        await check("tenant-deadline-observed-expiry-awaits-terminal-cancelled-audit", async () =>
        {
            var f = new Probe();
            var writing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            f.Business = (_, token) => throw f.ObserveExpiry(token);
            f.Append = async (entry, token) =>
            {
                if (entry.Stage != TenantAuditStage.Terminal) return;
                Require(!token.IsCancellationRequested && f.Disposed == 1 && f.Timer?.Disposed == true);
                writing.SetResult();
                await release.Task.WaitAsync(token);
            };
            var pending = Call(f.Build(audited: true).WithOperationDeadline(f.Deadline)).AsTask();
            try { await writing.Task.WaitAsync(Watchdog); Require(!pending.IsCompleted); }
            finally { release.TrySetResult(); }
            DeadlineFailure(await pending.WaitAsync(Watchdog));
            Outcomes(f, TenantAuditOutcome.Authorized, TenantAuditOutcome.Cancelled);
        });

        await check("tenant-deadline-nonexpired-success-and-denial-keep-audit-entries", async () =>
        {
            foreach (var allowed in new[] { true, false })
            {
                var f = new Probe { Allow = allowed };
                var original = f.Build(audited: true);
                var without = await Call(original);
                var baseline = f.Events.Select(Entry).ToArray();
                f.Events.Clear();
                var result = await Call(original.WithOperationDeadline(f.Deadline));
                Require(result.IsSuccess == without.IsSuccess);
                Require(allowed ? result.Value == without.Value : result.Error.Code == without.Error.Code);
                Require(f.Events.Select(Entry).SequenceEqual(baseline));
                if (allowed) Outcomes(f, TenantAuditOutcome.Authorized, TenantAuditOutcome.Succeeded);
                else Require(f.Events.Single() is { Stage: TenantAuditStage.Terminal, Outcome: TenantAuditOutcome.Denied, AuthorizedTenant: null } && f.Timer is null);
            }
        });

        await check("tenant-deadline-handler-returned-deadline-code-is-rejected-not-cancelled", async () =>
        {
            var f = new Probe();
            var handlerResult = OperationResult<int>.Failure(new(OperationDeadline.DeadlineExceededCode, "Handler result."));
            f.Business = (_, _) => { f.Expire(); return handlerResult; };
            var result = await Call(f.Build(audited: true).WithOperationDeadline(f.Deadline));
            Require(ReferenceEquals(result, handlerResult));
            Outcomes(f, TenantAuditOutcome.Authorized, TenantAuditOutcome.Rejected);
        });

        await check("tenant-deadline-expiry-and-required-audit-failure-retain-original-causes", async () =>
        {
            var f = new Probe();
            var persistence = new IOException("terminal write failed");
            f.Business = (_, token) => throw f.ObserveExpiry(token);
            f.Append = (entry, _) => entry.Stage == TenantAuditStage.Terminal ? ValueTask.FromException(persistence) : ValueTask.CompletedTask;
            var error = await Throws<AggregateException>(() => Call(f.Build(audited: true).WithOperationDeadline(f.Deadline)));
            Require(error.InnerExceptions.Count == 2 && ReferenceEquals(error.InnerExceptions[0], f.ObservedCancellation));
            Require(error.InnerExceptions[1] is TenantAuditWriteException { OperationMayHaveExecuted: true } audit && ReferenceEquals(audit.InnerException, persistence));
            Outcomes(f, TenantAuditOutcome.Authorized, TenantAuditOutcome.Cancelled);
        });

        await check("tenant-deadline-cleanup-aggregate-stays-faulted-and-audit-failure-keeps-aggregate", async () =>
        {
            var f = new Probe();
            var cleanup = new IOException("cleanup failed");
            var persistence = new IOException("terminal write failed");
            f.Business = (_, token) => throw f.ObserveExpiry(token);
            f.Cleanup = () => ValueTask.FromException(cleanup);
            f.Append = (entry, _) => entry.Stage == TenantAuditStage.Terminal ? ValueTask.FromException(persistence) : ValueTask.CompletedTask;
            var error = await Throws<AggregateException>(() => Call(f.Build(audited: true).WithOperationDeadline(f.Deadline)));
            Require(error.InnerExceptions.Count == 2 && error.InnerExceptions[0] is AggregateException { InnerExceptions.Count: 2 });
            var work = (AggregateException)error.InnerExceptions[0];
            Require(ReferenceEquals(work.InnerExceptions[0], f.ObservedCancellation) && ReferenceEquals(work.InnerExceptions[1], cleanup));
            Require(error.InnerExceptions[1] is TenantAuditWriteException { OperationMayHaveExecuted: true } audit && ReferenceEquals(audit.InnerException, persistence));
            Outcomes(f, TenantAuditOutcome.Authorized, TenantAuditOutcome.Faulted);
        });

        await check("tenant-deadline-preserves-possible-commit-fault-from-change-audit", async () =>
        {
            var f = new Probe();
            var mutations = 0;
            var recorder = TenantChangeAuditRecorder.Activate(new(TenantChangeAuditMode.MetadataOnly,
                [new("document.write", "write", "document")]), () => new UnusedChangeSink())!;
            var dispatcher = f.Build(audited: true, business: async (context, token) =>
            {
                var result = await recorder.ExecuteAsync<int>(context, mutationToken =>
                {
                    mutations++;
                    throw f.ObserveExpiry(mutationToken);
                }, token);
                return OperationResult<int>.Success(result);
            }).WithOperationDeadline(f.Deadline);
            var error = await Throws<TenantChangeAuditWriteException>(() => Call(dispatcher));
            Require(mutations == 1 && error.MayHaveCommitted && ReferenceEquals(error.InnerException, f.ObservedCancellation));
            Outcomes(f, TenantAuditOutcome.Authorized, TenantAuditOutcome.Faulted);
        });
    }

    private static object Entry(TenantAuditEvent e) => (e.ParentInvocationId, e.Stage, e.Outcome, e.ObservedAtUtc,
        e.OperationId, e.Action, e.ResourceKind, e.Principal, e.AuthorizedTenant, e.ResourceId);

    private static void Outcomes(Probe f, TenantAuditOutcome authorization, TenantAuditOutcome terminal)
    {
        Require(f.Events.Select(e => (e.Stage, e.Outcome)).SequenceEqual(
            [(TenantAuditStage.Authorization, authorization), (TenantAuditStage.Terminal, terminal)]));
        Require(f.Events[0].InvocationId == f.Events[1].InvocationId && f.Events.All(e => e.AuthorizedTenant?.Value == "A"));
    }

    private static void DeadlineFailure(OperationResult<int> result)
        => Require(result.Error is { Code: "operation.deadline_exceeded", Message: "The operation deadline expired." });

    private static ValueTask<OperationResult<int>> Call(TenantOperationDispatcher dispatcher, CancellationToken token = default)
        => dispatcher.DispatchAsync<string, int>("document.write", "shared", new("test-deadline"), token);

    private static async Task<T> Throws<T>(Func<ValueTask<OperationResult<int>>> action) where T : Exception
    {
        try { await action(); }
        catch (T error) { return error; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }

    private static void Require(bool condition, [CallerArgumentExpression(nameof(condition))] string? expression = null)
    { if (!condition) throw new InvalidOperationException(expression); }

    private sealed class Probe : TimeProvider, IIdentitySource, ITenantMembershipSource, IResourceScopeSource, ITenantAccessPolicy,
        IIdentitySessionStatusSource, ITenantAuditSink
    {
        internal readonly Clock Clock = new();
        internal readonly List<TenantAuditEvent> Events = [];
        internal bool Allow = true;
        internal int Created, Disposed;
        internal Action<CancellationToken>? Authority;
        internal Action? Factory;
        internal Func<TenantAuditEvent, CancellationToken, ValueTask>? Append;
        internal Func<ValueTask>? Cleanup;
        internal Func<TenantOperationContext, CancellationToken, OperationResult<int>> Business = (_, _) => OperationResult<int>.Success(42);
        internal OperationCanceledException? ObservedCancellation;
        internal ManualTimer? Timer { get; private set; }
        internal OperationDeadline Deadline => new(TimeSpan.FromTicks(1), this);
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) => Timer = new(callback, state);
        internal void Expire() => (Timer ?? throw new InvalidOperationException("No deadline timer.")).Fire();
        internal OperationCanceledException ObserveExpiry(CancellationToken token)
        { Expire(); Require(token.IsCancellationRequested); return ObservedCancellation = new OperationCanceledException(token); }

        internal TenantOperationDispatcher Build(bool audited = false, bool observed = false,
            OperationConcurrencyBudget? budget = null, TenantOperationConcurrencyBudget? tenantBudget = null,
            Func<TenantOperationContext, CancellationToken, ValueTask<OperationResult<int>>>? business = null)
        {
            var registry = new TenantOperationRegistryBuilder().Register<string, int>("document.write", "write", "document", s => s,
                s => new(new("A"), s), () => { Created++; Factory?.Invoke(); return new Handler(this, business); }).Build();
            var identity = observed ? new SessionValidatingIdentitySource(this, this, TimeSpan.FromSeconds(1), Clock) : (IIdentitySource)this;
            var services = new TenantSecurityServices(identity, new(["issuer"], ["fixture"]), this, this,
                TimeSpan.FromSeconds(30), this, Clock, audited ? new TenantAuditServices(this, TimeSpan.FromMinutes(1)) : null);
            return tenantBudget is not null ? TenantOperationDispatcher.WithTenantConcurrencyBudget(registry, services, tenantBudget)
                : budget is not null ? new(registry, services, budget) : new(registry, services);
        }

        public ValueTask<IdentitySnapshot?> ResolveAsync(CancellationToken token)
        { Authority?.Invoke(token); return ValueTask.FromResult<IdentitySnapshot?>(new(new("issuer", "alice"), "fixture", Fixture.Now.AddMinutes(-1), Fixture.Now.AddHours(1), "session")); }
        ValueTask<SessionStatusSnapshot?> IIdentitySessionStatusSource.ResolveAsync(IdentitySnapshot identity, CancellationToken token)
            => ValueTask.FromResult<SessionStatusSnapshot?>(new(identity.Principal, identity.AuthenticationScheme, identity.SessionId!, true, identity.NotBeforeUtc, identity.ExpiresAtUtc));
        public ValueTask<TenantMembershipSnapshot?> ResolveAsync(IdentitySnapshot identity, TenantId tenant, CancellationToken token)
        { Authority?.Invoke(token); return ValueTask.FromResult<TenantMembershipSnapshot?>(new(identity.Principal, tenant, true, Fixture.Now.AddMinutes(-1), Fixture.Now.AddHours(1), "membership")); }
        public ValueTask<ResourceScopeSnapshot?> ResolveAsync(TenantId tenant, string kind, string id, CancellationToken token)
        { Authority?.Invoke(token); return ValueTask.FromResult<ResourceScopeSnapshot?>(new(tenant, kind, id, "version")); }
        public ValueTask<bool> IsAllowedAsync(TenantAccessRequest request, CancellationToken token)
        { Authority?.Invoke(token); return ValueTask.FromResult(Allow); }
        public ValueTask AppendAsync(TenantAuditEvent entry, CancellationToken token)
        { Events.Add(entry); return Append?.Invoke(entry, token) ?? ValueTask.CompletedTask; }
    }

    private sealed class Handler(Probe probe, Func<TenantOperationContext, CancellationToken, ValueTask<OperationResult<int>>>? business)
        : ITenantOperationHandler<string, int>, IAsyncDisposable
    {
        public ValueTask<OperationResult<int>> HandleAsync(string request, TenantOperationContext context, CancellationToken token)
            => business?.Invoke(context, token) ?? ValueTask.FromResult(probe.Business(context, token));
        public async ValueTask DisposeAsync() { if (probe.Cleanup is not null) await probe.Cleanup(); probe.Disposed++; }
    }

    private sealed class ManualTimer(TimerCallback callback, object? state) : ITimer
    {
        internal bool Disposed { get; private set; }
        internal void Fire() { if (!Disposed) callback(state); }
        public bool Change(TimeSpan dueTime, TimeSpan period) => throw new NotSupportedException();
        public void Dispose() => Disposed = true;
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }

    private sealed class UnusedChangeSink : ITenantChangeAuditSink
    {
        public ValueTask AppendAsync(TenantChangeAuditRecord record, CancellationToken token) => throw new InvalidOperationException("No receipt was returned.");
    }
}
