# Experimental Core contracts

This first `net11.0` slice has no package, host, protocol or provider references.
It is not an identity/tenant enforcement boundary. `OperationContext` contains
caller-supplied correlation metadata only; do not treat its fields as authority.

Register typed handler factories, snapshot an `OperationRegistry`, then dispatch
by canonical operation ID. Unknown operations and request/response type mismatches
return intentional structured errors without invoking a factory. A supplied
already-canceled token throws before any factory. Cancellation during execution is
cooperative: handlers must honor the token. Unexpected exceptions propagate to the
adapter; they are not relabeled as business failures or exposed as response text.

Every factory call transfers ownership of a **new** handler to the dispatcher.
The dispatcher awaits its result and then disposes `IAsyncDisposable` (preferred)
or `IDisposable` on success, failure, exception or cancellation. Do not return
pooled/shared/container-owned handlers through this ownership contract. If execution
and cleanup both fail, an `AggregateException` preserves both, execution first.
Cleanup-only failures propagate. No retry, cancellation timer or background task
is created by Core. A successful null response is permitted for nullable contracts;
a null result object from a faulty handler is rejected and the handler disposed.

Registry/context collections are defensive snapshots. The builder is for single
startup-thread use; registries and dispatchers may be shared concurrently. Handler
instances, business payload objects and factory-captured state are caller-owned
design responsibilities; Core does not deep-clone or serialize them. Registration
does not construct a handler, and generated registration must preserve that rule.

The attribute and `IOperationHandlerFactory` are the compile-time bridge for the
separate code generator. Core does not scan assemblies or use runtime activation.
Release/provider/platform qualification and benchmarks remain separate gates;
these contracts do not make unsupported Windows or hosted CI pass.

## Dispatch duration

Hosts can subscribe to the `cehpalon.operation.dispatch.duration` histogram on
the `Cehpalon.Engine.Core` meter. It reports seconds for registered, type-matched
dispatches through owned handler cleanup. Input validation, registry lookup,
contract mismatch and cancellation before dispatch are outside the measurement.
A request can invoke Core more than once through layered dispatchers, so use the
histogram to count Core invocations and track transport requests separately.

The single `outcome` tag is `success`, `failure` for a business error result,
`canceled` for an execution cancellation carrying the requested caller token,
or `fault`. Factory or handler exceptions outside caller cancellation and all
cleanup errors count as `fault`. Measurements carry no IDs, payloads or
exception data.

Core publishes the histogram on the first eligible dispatch and starts timing
only when a listener enables it. Concurrent or reentrant dispatches during
publication proceed without timing. A publication callback that throws leaves
this process without the histogram until restart. Exceptions from measurement
callbacks preserve the dispatch result and owned cleanup behavior. Listener
callbacks run synchronously and should complete promptly.
