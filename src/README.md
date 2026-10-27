# Engine source

The first experimental implementation slices are:

- [Core](Cehpalon.Engine.Core/README.md): typed request/reply contracts, immutable registry/context, lazy owned handlers and guarded dispatch/cancellation/cleanup. Correlation metadata is not tenant authority.
- [Composition](Cehpalon.Engine.Composition/README.md): strict startup appsettings compiler and immutable graph before activation, explicit-false rules, preflight/owned lifecycle/rollback. No host/provider activation is built in.
- [CodeGeneration](Cehpalon.Engine.CodeGeneration/README.md): compile-time typed operation registration through an opt-in incremental analyzer. Roslyn is not a Core/consumer runtime dependency.

These are bounded components, not full W1 feature acceptance or certified transports/providers. All umbrella features remain M0; #3 supported-Windows qualification remains Blocked. Identity/tenancy, real adapters and broader profiles require their own implementations and conformance evidence.
