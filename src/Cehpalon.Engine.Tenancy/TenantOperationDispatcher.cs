using System.Collections.ObjectModel;
using System.Runtime.ExceptionServices;
using Cehpalon.Engine.Core;
using Cehpalon.Engine.Identity;

namespace Cehpalon.Engine.Tenancy;

public sealed class TenantSecurityServices
{
    public TenantSecurityServices(IIdentitySource identitySource,IdentityTrustPolicy identityTrust,ITenantMembershipSource memberships,IResourceScopeSource resources,TimeSpan maximumLeaseLifetime,ITenantAccessPolicy? policy=null,TimeProvider? timeProvider=null,TenantAuditServices? audit=null)
    {
        IdentitySource=identitySource??throw new ArgumentNullException(nameof(identitySource)); IdentityTrust=identityTrust??throw new ArgumentNullException(nameof(identityTrust));
        Memberships=memberships??throw new ArgumentNullException(nameof(memberships)); Resources=resources??throw new ArgumentNullException(nameof(resources));
        if(maximumLeaseLifetime<=TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(maximumLeaseLifetime));
        MaximumLeaseLifetime=maximumLeaseLifetime; Policy=policy??DenyPolicy.Instance; TimeProvider=timeProvider??TimeProvider.System;
        Audit=audit;
    }
    public IIdentitySource IdentitySource { get; }
    public IdentityTrustPolicy IdentityTrust { get; }
    public ITenantMembershipSource Memberships { get; }
    public IResourceScopeSource Resources { get; }
    public TimeSpan MaximumLeaseLifetime { get; }
    public ITenantAccessPolicy Policy { get; }
    public TimeProvider TimeProvider { get; }
    public TenantAuditServices? Audit { get; }
    private sealed class DenyPolicy:ITenantAccessPolicy
    {
        internal static readonly DenyPolicy Instance=new();
        public ValueTask<bool> IsAllowedAsync(TenantAccessRequest request,CancellationToken cancellationToken)=>ValueTask.FromResult(false);
    }
}

/// <summary>Trusted registration phase; callbacks must be pure and freeze must produce a defensive request snapshot.</summary>
public sealed class TenantOperationRegistryBuilder
{
    private readonly Dictionary<string,ITenantRegistration> registrations=new(StringComparer.Ordinal);
    public TenantOperationRegistryBuilder Register<TRequest,TResponse>(string operationId,string action,string kind,Func<TRequest,TRequest> freezeRequest,Func<TRequest,TenantTarget> selectTarget,Func<ITenantOperationHandler<TRequest,TResponse>> factory)
    {
        OperationId.RequireValid(operationId); OperationId.RequireValid(action); OperationId.RequireValid(kind);
        ArgumentNullException.ThrowIfNull(freezeRequest); ArgumentNullException.ThrowIfNull(selectTarget); ArgumentNullException.ThrowIfNull(factory);
        if(!registrations.TryAdd(operationId,new TenantRegistration<TRequest,TResponse>(operationId,action,kind,freezeRequest,selectTarget,factory))) throw new ArgumentException("Duplicate operation ID.",nameof(operationId));
        return this;
    }
    public TenantOperationRegistry Build()=>new(registrations);
}

/// <summary>Immutable registration metadata only; descriptions are not authorization capabilities.</summary>
public sealed class TenantOperationDescription
{
    internal TenantOperationDescription(string operationId, string action, string resourceKind)
        => (OperationId, Action, ResourceKind) = (operationId, action, resourceKind);
    public string OperationId { get; }
    public string Action { get; }
    public string ResourceKind { get; }
}

public sealed class TenantOperationRegistry
{
    private readonly IReadOnlyDictionary<string,ITenantRegistration> registrations;
    internal TenantOperationRegistry(Dictionary<string,ITenantRegistration> registrations)
    {
        this.registrations=new ReadOnlyDictionary<string,ITenantRegistration>(new Dictionary<string,ITenantRegistration>(registrations,StringComparer.Ordinal));
        OperationIds=Array.AsReadOnly(registrations.Keys.Order(StringComparer.Ordinal).ToArray());
    }
    public IReadOnlyList<string> OperationIds { get; }
    /// <summary>Describe actual frozen registrations in ordinal operation order without invoking callbacks.</summary>
    public IReadOnlyList<TenantOperationDescription> DescribeOperations()
        => Array.AsReadOnly(OperationIds.Select(id =>
        {
            var registered = registrations[id];
            return new TenantOperationDescription(id, registered.Action, registered.ResourceKind);
        }).ToArray());
    internal void DemandContextBinding(TenantOperationContext context)
    {
        context.DemandRegistryBinding(this);
        if (!registrations.TryGetValue(context.OperationId, out var registered)) throw new TenantAccessDeniedException();
        context.DemandBinding(registered.Origin, context.OperationId);
    }
    internal bool TryGet(string operationId,out ITenantRegistration? registration)=>registrations.TryGetValue(operationId,out registration);
}

public sealed class TenantOperationDispatcher
{
    private readonly TenantOperationRegistry registry;
    private readonly TenantSecurityServices services;
    private readonly OperationConcurrencyBudget? budget;
    private readonly TenantOperationConcurrencyBudget? tenantBudget;
    private readonly OperationDeadline? deadline;
    public TenantOperationDispatcher(TenantOperationRegistry registry,TenantSecurityServices services)
    { this.registry=registry??throw new ArgumentNullException(nameof(registry)); this.services=services??throw new ArgumentNullException(nameof(services)); }

    /// <summary>Apply a shared capacity budget after fresh authorization and through owned-handler cleanup.</summary>
    public TenantOperationDispatcher(TenantOperationRegistry registry, TenantSecurityServices services, OperationConcurrencyBudget budget)
        : this(registry, services) => this.budget = budget ?? throw new ArgumentNullException(nameof(budget));

    private TenantOperationDispatcher(TenantOperationRegistry registry, TenantSecurityServices services,
        TenantOperationConcurrencyBudget tenantBudget)
        : this(registry, services) => this.tenantBudget = tenantBudget ?? throw new ArgumentNullException(nameof(tenantBudget));

    private TenantOperationDispatcher(TenantOperationDispatcher source, OperationDeadline deadline)
        => (registry, services, budget, tenantBudget, this.deadline) =
            (source.registry, source.services, source.budget, source.tenantBudget, deadline);

    /// <summary>Copy this dispatcher with a cooperative deadline for authorized business work. Experimental (M0).</summary>
    public TenantOperationDispatcher WithOperationDeadline(OperationDeadline deadline)
        => new(this, deadline ?? throw new ArgumentNullException(nameof(deadline)));

    /// <summary>Share a total and per-tenant capacity budget across protected dispatchers.</summary>
    public static TenantOperationDispatcher WithTenantConcurrencyBudget(
        TenantOperationRegistry registry, TenantSecurityServices services, TenantOperationConcurrencyBudget tenantBudget)
        => new(registry, services, tenantBudget);

    public ValueTask<OperationResult<TResponse>> DispatchAsync<TRequest,TResponse>(string operationId,TRequest request,OperationContext correlation,CancellationToken cancellationToken=default)
    {
        OperationId.RequireValid(operationId); ArgumentNullException.ThrowIfNull(correlation); cancellationToken.ThrowIfCancellationRequested();
        if(!registry.TryGet(operationId,out var registration)) return ValueTask.FromResult(OperationResult<TResponse>.Failure(new("operation.not_found","The requested operation is not registered.")));
        if(registration is not TenantRegistration<TRequest,TResponse> typed) return ValueTask.FromResult(OperationResult<TResponse>.Failure(new("operation.contract_mismatch","The requested operation does not use these request/response contracts.")));
        return typed.DispatchAsync(request,correlation,services,budget,tenantBudget,deadline,cancellationToken,registry);
    }
}

internal interface ITenantRegistration
{
    string Action { get; }
    string ResourceKind { get; }
    object Origin { get; }
}

internal sealed class TenantRegistration<TRequest,TResponse> : ITenantRegistration
{
    private readonly string operationId,action,kind;
    private readonly Func<TRequest,TRequest> freeze;
    private readonly Func<TRequest,TenantTarget> select;
    private readonly object origin=new();
    private readonly OperationDispatcher core;
    string ITenantRegistration.Action => action;
    string ITenantRegistration.ResourceKind => kind;
    object ITenantRegistration.Origin => origin;
    internal TenantRegistration(string operationId,string action,string kind,Func<TRequest,TRequest> freeze,Func<TRequest,TenantTarget> select,Func<ITenantOperationHandler<TRequest,TResponse>> factory)
    {
        this.operationId=operationId; this.action=action; this.kind=kind; this.freeze=freeze; this.select=select;
        core=new(new OperationRegistryBuilder().Register<AuthorizedInvocation,TResponse>(operationId,()=>new HandlerBridge(factory,origin,operationId)).Build());
    }
    internal ValueTask<OperationResult<TResponse>> DispatchAsync(TRequest request,OperationContext correlation,TenantSecurityServices services,
        OperationConcurrencyBudget? budget,TenantOperationConcurrencyBudget? tenantBudget,OperationDeadline? deadline,CancellationToken token,TenantOperationRegistry owningRegistry)
    {
        var bounded = deadline is null ? null : new DeadlineDispatch(deadline);
        return services.Audit is null
            ? DispatchCoreAsync(request,correlation,services,budget,tenantBudget,bounded,token,null,owningRegistry)
            : DispatchAuditedAsync(request,correlation,services,budget,tenantBudget,bounded,token,owningRegistry);
    }

    private async ValueTask<OperationResult<TResponse>> DispatchAuditedAsync(TRequest request,OperationContext correlation,TenantSecurityServices services,
        OperationConcurrencyBudget? budget,TenantOperationConcurrencyBudget? tenantBudget,DeadlineDispatch? bounded,CancellationToken token,TenantOperationRegistry owningRegistry)
    {
        var id=Guid.NewGuid(); TenantAccessRequest? authority=null; var mayHaveExecuted=false;
        TenantAuditWriteException? authorizationAuditFailure=null;
        TenantAuditEvent Entry(TenantAuditStage stage,TenantAuditOutcome outcome)=>new(id,stage,outcome,
            services.TimeProvider.GetUtcNow(),operationId,action,kind,authority?.Identity.Principal,authority?.Tenant,authority?.Resource.ResourceId,services.Audit?.ParentInvocationId);
        async ValueTask Authorized(TenantAccessRequest access)
        {
            authority=access;
            try { await services.Audit!.AppendAsync(Entry(TenantAuditStage.Authorization,TenantAuditOutcome.Authorized),false).ConfigureAwait(false); }
            catch(TenantAuditWriteException failure) { authorizationAuditFailure=failure; throw; }
            mayHaveExecuted=true;
        }
        OperationResult<TResponse>? result=null; Exception? executionFailure=null;
        try { result=await DispatchCoreAsync(request,correlation,services,budget,tenantBudget,bounded,token,Authorized,owningRegistry).ConfigureAwait(false); }
        catch(TenantAuditWriteException failure) when(ReferenceEquals(failure,authorizationAuditFailure)) { throw; }
        catch(Exception failure) { executionFailure=failure; }

        // Required audit keeps the original cancellation, even when Core translates expiry or
        // gives caller cancellation precedence. Non-cancellation faults still take priority.
        var operationFailure = executionFailure is null or OperationCanceledException
            ? bounded?.DispatchCancellation ?? executionFailure : executionFailure;
        var outcome=operationFailure is OperationCanceledException cancellation &&
            (bounded is null || executionFailure is null || bounded.IsExpectedCancellation(cancellation,token))
            ?TenantAuditOutcome.Cancelled:
            operationFailure is not null?TenantAuditOutcome.Faulted:
            result!.IsSuccess?TenantAuditOutcome.Succeeded:
            result.Error!.Code=="access.denied"?TenantAuditOutcome.Denied:TenantAuditOutcome.Rejected;
        try { await services.Audit!.AppendAsync(Entry(TenantAuditStage.Terminal,outcome),mayHaveExecuted).ConfigureAwait(false); }
        catch(TenantAuditWriteException auditFailure) when(operationFailure is not null)
        { throw new AggregateException("Operation and required audit persistence failed.",operationFailure,auditFailure); }
        if(executionFailure is not null) ExceptionDispatchInfo.Capture(executionFailure).Throw();
        if(bounded?.DispatchCancellation is not null) token.ThrowIfCancellationRequested();
        return result!.IsSuccess && authority is not null && !authority.Identity.SatisfiesObservationConstraints()
            ? Denied() : result!;
    }

    private async ValueTask<OperationResult<TResponse>> DispatchCoreAsync(TRequest request,OperationContext correlation,TenantSecurityServices services,
        OperationConcurrencyBudget? budget,TenantOperationConcurrencyBudget? tenantBudget,DeadlineDispatch? bounded,CancellationToken token,
        Func<TenantAccessRequest,ValueTask>? authorized,TenantOperationRegistry owningRegistry)
    {
        token.ThrowIfCancellationRequested();
        var frozen=freeze(request);
        var target=select(frozen)??throw new InvalidOperationException("Target selector returned null.");
        token.ThrowIfCancellationRequested();
        var identity=await services.IdentitySource.ResolveAsync(token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if(!services.IdentityTrust.IsAccepted(identity,services.TimeProvider.GetUtcNow())) return Denied();
        var membership=await services.Memberships.ResolveAsync(identity!,target.Tenant,token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if(!ValidMembership(identity!,target.Tenant,membership,services.TimeProvider.GetUtcNow())) return Denied();
        var resource=await services.Resources.ResolveAsync(target.Tenant,kind,target.ResourceId,token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if(resource is null || !resource.Tenant.Equals(target.Tenant) || !StringComparer.Ordinal.Equals(resource.Kind,kind) || !StringComparer.Ordinal.Equals(resource.ResourceId,target.ResourceId)) return Denied();
        var policyTime=services.TimeProvider.GetUtcNow();
        if(!services.IdentityTrust.IsAccepted(identity,policyTime) || !ValidMembership(identity!,target.Tenant,membership,policyTime)) return Denied();
        var access=new TenantAccessRequest(operationId,action,identity!,membership!,resource);
        var policyExpires=DateTimeOffset.MaxValue;
        var policyNotBefore=policyTime;
        bool allowed;
        if(services.Policy is ILeasedTenantAccessPolicy leasedPolicy)
        {
            var decision=await leasedPolicy.EvaluateAsync(access,token).ConfigureAwait(false);
            allowed=decision is not null && decision.IsAllowed;
            if(allowed) { policyExpires=decision!.ExpiresAtUtc; policyNotBefore=decision.NotBeforeUtc>policyTime?decision.NotBeforeUtc:policyTime; }
        }
        else allowed=await services.Policy.IsAllowedAsync(access,token).ConfigureAwait(false);
        if(!allowed || !identity!.SatisfiesObservationConstraints()) { token.ThrowIfCancellationRequested(); return Denied(); }
        token.ThrowIfCancellationRequested();
        if(authorized is not null) await authorized(access).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        var now=services.TimeProvider.GetUtcNow();
        if(!services.IdentityTrust.IsAccepted(identity,now) || !ValidMembership(identity!,target.Tenant,membership,now) || now<policyNotBefore || now>=policyExpires) return Denied();
        var maximum=services.MaximumLeaseLifetime.Ticks>DateTimeOffset.MaxValue.Ticks-now.UtcTicks?DateTimeOffset.MaxValue:now.Add(services.MaximumLeaseLifetime);
        var expires=identity!.ExpiresAtUtc<membership!.ExpiresAtUtc?identity.ExpiresAtUtc:membership.ExpiresAtUtc;
        if(maximum<expires) expires=maximum;
        if(policyExpires<expires) expires=policyExpires;
        using var lease=new AuthorizationLease(services.TimeProvider,now,expires,identity);
        var context=new TenantOperationContext(access,correlation,lease,origin,owningRegistry);
        try
        {
            var invocation = new AuthorizedInvocation(frozen, context, bounded);
            var work = bounded is null
                ? DispatchBusinessAsync(invocation, resource.Tenant, correlation, budget, tenantBudget, token)
                : DispatchWithDeadlineAsync(bounded, invocation, resource.Tenant, correlation, budget, tenantBudget, token);
            // Core returns after owned cleanup. Never replace an explicit business failure or cleanup fault.
            var result = await work.ConfigureAwait(false);
            return result.IsSuccess && !identity.SatisfiesObservationConstraints() ? Denied() : result;
        }
        catch(TenantAccessDeniedException) { return Denied(); }
    }

    private ValueTask<OperationResult<TResponse>> DispatchBusinessAsync(AuthorizedInvocation invocation, TenantId tenant,
        OperationContext correlation, OperationConcurrencyBudget? budget, TenantOperationConcurrencyBudget? tenantBudget, CancellationToken token)
        => tenantBudget is not null
            ? tenantBudget.DispatchAsync<AuthorizedInvocation,TResponse>(tenant, core, operationId, invocation, correlation, token)
            : budget is null
                ? core.DispatchAsync<AuthorizedInvocation,TResponse>(operationId, invocation, correlation, token)
                : budget.DispatchAsync<AuthorizedInvocation,TResponse>(core, operationId, invocation, correlation, token);

    private ValueTask<OperationResult<TResponse>> DispatchWithDeadlineAsync(DeadlineDispatch bounded, AuthorizedInvocation invocation,
        TenantId tenant, OperationContext correlation, OperationConcurrencyBudget? budget,
        TenantOperationConcurrencyBudget? tenantBudget, CancellationToken token)
        => bounded.ExecuteAsync(linked => DispatchBusinessAsync(invocation, tenant, correlation, budget, tenantBudget, linked),
            () => invocation.Context.DemandBinding(origin,operationId), token);

    private static bool ValidMembership(IdentitySnapshot identity,TenantId tenant,TenantMembershipSnapshot? membership,DateTimeOffset now)
        =>membership is not null && membership.IsActive && membership.Principal.Equals(identity.Principal) && membership.Tenant.Equals(tenant) && membership.NotBeforeUtc<=now && now<membership.ExpiresAtUtc;
    private static OperationResult<TResponse> Denied()=>OperationResult<TResponse>.Failure(new("access.denied","Access denied."));
    private sealed record AuthorizedInvocation(TRequest Request,TenantOperationContext Context,DeadlineDispatch? Deadline);

    // Per invocation. OperationDeadline owns the public result; this records the cause for audit.
    private sealed class DeadlineDispatch(OperationDeadline deadline)
    {
        private CancellationToken operationToken;
        internal OperationCanceledException? DispatchCancellation { get; private set; }
        internal OperationCanceledException? CleanupCancellation { get; set; }
        internal bool HandlerFactoryStarted { get; set; }

        internal bool IsExpectedCancellation(OperationCanceledException error, CancellationToken caller)
            => !ReferenceEquals(error,CleanupCancellation) &&
                (error.CancellationToken == operationToken && operationToken.IsCancellationRequested ||
                 error.CancellationToken == caller && caller.IsCancellationRequested);

        internal async ValueTask<OperationResult<TResponse>> ExecuteAsync(
            Func<CancellationToken,ValueTask<OperationResult<TResponse>>> dispatch, Action demandBinding, CancellationToken caller)
        {
            try
            {
                return await deadline.ExecuteAsync<TResponse>(async linked =>
                {
                    operationToken = linked;
                    try { return await dispatch(linked).ConfigureAwait(false); }
                    catch (OperationCanceledException error)
                    {
                        // A lone cleanup fault can reuse the linked token. Keep it out of the deadline classifier.
                        // Core's execution+cleanup aggregate never enters this catch.
                        if (ReferenceEquals(error,CleanupCancellation)) throw new CleanupCancellationFault(error);
                        // Core or the budget can observe expiry before the bridge gets to demand the lease.
                        if (!HandlerFactoryStarted && error.CancellationToken == linked && linked.IsCancellationRequested && !caller.IsCancellationRequested)
                            demandBinding();
                        DispatchCancellation = error;
                        throw;
                    }
                }, caller).ConfigureAwait(false);
            }
            catch (CleanupCancellationFault error)
            {
                ExceptionDispatchInfo.Capture(error.Cause).Throw();
                throw;
            }
        }

        private sealed class CleanupCancellationFault(OperationCanceledException cause) : Exception
        {
            internal OperationCanceledException Cause { get; } = cause;
        }
    }

    private sealed class HandlerBridge(Func<ITenantOperationHandler<TRequest,TResponse>> factory,object origin,string operationId):IOperationHandler<AuthorizedInvocation,TResponse>,IAsyncDisposable
    {
        private ITenantOperationHandler<TRequest,TResponse>? owned;
        private DeadlineDispatch? deadline;
        public async ValueTask<OperationResult<TResponse>> HandleAsync(AuthorizedInvocation request,OperationContext correlation,CancellationToken token)
        {
            deadline=request.Deadline;
            request.Context.DemandBinding(origin,operationId); token.ThrowIfCancellationRequested();
            if(deadline is not null) deadline.HandlerFactoryStarted=true;
            owned=factory()??throw new InvalidOperationException("The tenant handler factory returned null.");
            request.Context.DemandBinding(origin,operationId); token.ThrowIfCancellationRequested();
            return await owned.HandleAsync(request.Request,request.Context,token).ConfigureAwait(false);
        }
        public async ValueTask DisposeAsync()
        {
            try
            {
                if(owned is IAsyncDisposable asyncDisposable) await asyncDisposable.DisposeAsync().ConfigureAwait(false);
                else if(owned is IDisposable disposable) disposable.Dispose();
            }
            catch(TenantAccessDeniedException failure)
            {
                // Cleanup is an infrastructure/lifecycle failure, not a fresh authorization denial.
                // Preserve its cause without letting the outer semantic-denial mapper hide it.
                throw new InvalidOperationException("Owned tenant handler cleanup failed.",failure);
            }
            catch(OperationCanceledException failure) when(deadline is not null)
            {
                deadline.CleanupCancellation=failure;
                throw;
            }
        }
    }
}
