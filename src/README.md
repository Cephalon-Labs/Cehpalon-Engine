# Engine source

The first experimental implementation slices are:

- [Core](Cehpalon.Engine.Core/README.md): typed request/reply contracts, immutable registry/context, lazy owned handlers and guarded dispatch/cancellation/cleanup. Correlation metadata is not tenant authority.
- [Composition](Cehpalon.Engine.Composition/README.md): strict startup appsettings compiler and immutable graph before activation, explicit-false rules, preflight/owned lifecycle/rollback. No host/provider activation is built in.
- [CodeGeneration](Cehpalon.Engine.CodeGeneration/README.md): compile-time typed operation registration through an opt-in incremental analyzer. Roslyn is not a Core/consumer runtime dependency.
- [Identity.Contracts](Cehpalon.Engine.Identity.Contracts/README.md): immutable identity representations and exact trust/time acceptance for configured sources; no JWT parser/authentication implementation.
- [Tenancy](Cehpalon.Engine.Tenancy/README.md): fresh authority checks, immutable checked scope and owned tenant handlers over a private Core bridge. Caller metadata is not authority; provider checks and protocol adapters remain required.

These are bounded components, not full W1 feature acceptance or certified transports/providers. All umbrella features remain M0; #3 supported-Windows qualification remains Blocked. Real identity/tenant provider adapters and broader profiles require their own implementation and conformance evidence.
