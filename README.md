# Cehpalon Engine

Private MIT-licensed .NET engine program. Baseline: 2026-10-27.

Goal: a small host-agnostic core with optional modules, reusable business handlers and configuration-selected supported transports, topology and capability profiles.

**Status: planning and repository bootstrap. No runtime implementation, passing runtime test suite, performance result or production maturity is claimed.**

- [Project](https://github.com/orgs/Cephalon-Labs/projects/4)
- [Engine issues](https://github.com/Cephalon-Labs/Cehpalon-Engine/issues)
- [Plan and documentation](https://github.com/Cephalon-Labs/Cehpalon-Engine-Docs)
- [Three sample lanes](https://github.com/Cephalon-Labs/Cehpalon-Engine-Samples)
- [Shared AI rules](https://github.com/Cephalon-Labs/Cehpalon-Engine-Docs/blob/main/skills/cehpalon-engine/SKILL.md)

## Layout

- `src/`: future core, composition and optional adapters
- `Tests/`: future independently runnable contract/integration/conformance suites
- `Benchmarks/`: future benchmark suites with predeclared protocols and raw evidence
- `AGENTS.md`, `CLAUDE.md`, `GEMINI.md`: entry points to canonical program guidance

.NET 11 is the implementation research target; qualify the current RC3 SDK and providers before pinning it. .NET 12 is a separate research track. No SDK is pinned in this bootstrap.

For Qwen, DeepSeek or another tool that does not discover these entry files, explicitly supply AGENTS.md and the linked shared skill/memory. Automatic support for a vendor tool is not claimed without a tested integration.
