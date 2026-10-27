# Experimental tenant operation boundary

This BCL-only assembly references Core and Identity.Contracts. It is an in-process
request/reply boundary, not a JWT/OIDC validator, provider sandbox, RLS implementation,
distributed revocation service or real transport certification. Full #7/#8 acceptance
and #3 supported-Windows qualification remain open; umbrella maturity remains M0.

`TenantOperationRegistryBuilder.Register<TRequest,TResponse>` binds an operation ID,
action and resource kind in trusted startup code. A required pure `freezeRequest`
callback must create a **deep defensive snapshot** of the request; `selectTarget`
derives the untrusted tenant/resource selection from that same frozen request.
The engine cannot infer deep immutability of arbitrary application payloads. Callers
must not retain mutable aliases to the frozen snapshot; handlers must use the checked
context for provider access rather than treating payload/metadata as authority.

`TenantOperationDispatcher` resolves a fresh configured identity, validates its
explicit issuer/scheme trust and time interval, resolves membership bound to the
exact issuer+subject and selected tenant, resolves resource ownership, and finally
checks the configured policy. Missing policy denies. Requested tenant is supplied
to resource lookup because resource IDs can be tenant-local; the returned actual
tenant/kind/ID must still match. Semantic denials return the same `access.denied`
error and do not create a business handler. Core correlation metadata supplies no
identity, tenant, role or policy authority.

After authorization the boundary mints an internal operation-bound lease, then
bridges into Core through a private invocation type/registry. Handler factories
execute only after lease and cancellation guards and transfer a **new owned handler**.
Core disposes the bridge and its handler on success/failure/cancellation; execution
and cleanup errors are both retained. A cleanup-thrown access-denied exception is
wrapped as an unexpected cleanup fault with the original cause, so it cannot be
mistaken for a new semantic authorization denial after business work. Cancellation is cooperative, not forced
interruption of application code. Unexpected authority/policy faults propagate
without business work; real transports must map/sanitize those failures themselves.

`TenantOperationContext` has no public minting constructor. Its immutable identity,
membership, resource, operation/action and correlation are bound to one invocation.
`DemandAccess(tenant,kind,id,action)` rejects a different tuple, an expired lease or
a context retained after completion. Validity is bounded by the identity/member
expiry and an explicit maximum lease lifetime. Clock rollback before lease creation
time also rejects access. Monotonic elapsed time additionally caps the effective
lease lifetime, so a smaller UTC rollback cannot extend it. Negative elapsed time,
observed expiry/invalidity or a clock fault permanently revokes the lease; clock
recovery cannot resurrect it. This is an in-process capability for trusted application
code, not protection against reflection, malicious handlers or code that bypasses
the checked provider boundary.

Authoritative sources and policies are trusted configured adapters. Constructing
public snapshot DTOs does not authenticate anyone. A protocol adapter must use an
established implementation to validate signature, issuer, audience, token type,
lifetime and subject before emitting identity. No tokens or claim bags are stored
in these contracts. The library refreshes sources per invocation but has no atomic
transaction with provider data or external revocation: a membership/resource can
change after the check. Provider adapters must enforce scoped keys, conditional
versions/transactions/RLS and cleanup appropriate to their own semantics.

No `AsyncLocal` or ambient principal/tenant is used. Registration builders are for
single-writer startup; their built registries are snapshots safe for concurrent
reads. Auth source implementations themselves must support the application's
concurrency/lifetime model. API/Worker/cache/retrieval/event entrypoints must use
this boundary explicitly; the existence of these contracts does not certify those
paths or automatically secure an ordinary unguarded Core registry.
