# Cehpalon Engine

Private MIT-licensed .NET engine program. Baseline: 2026-10-27.

Goal: a small host-agnostic core with optional modules, reusable business handlers and configuration-selected supported transports, topology and capability profiles.

**Status: Engine runtime features remain unimplemented/M0. Scoped SDK/platform/third-party qualification is separate from Engine feature, performance or production proof.**

[PlatformQualification](Tests/PlatformQualification/README.md) now pins the private-tested .NET11 RC3 SDK and a locked driver graph. The bounded HTTPS/JWT/GraphQL and owned PostgreSQL/RabbitMQ smoke has explicit proof limits and retained failures. Local Windows26H2 is observational compatibility because that version is not in the checked support table; Linux qualification uses listed Ubuntu22.04.5 under WSL. See the qualification evidence and CI results for the exact host/revision tested.

W0 has [selected sample profiles and predeclared proof contracts](docs/w0-profiles.md). Provider selection and bounded qualification tooling are in place; Engine adapters and Samples still require their own implementation and conformance. All Engine runtime features remain M0.

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

.NET11 SDK `11.0.100-rc.3.26522.148` is pinned in `global.json` with roll-forward disabled. Use a matching private SDK or CI setup; unrelated system SDKs were not replaced. Direct driver versions and the qualification lock graph are explicit. .NET12 remains a separate research track. Package distribution/license review is required before release; the complete third-party graph must not be described as MIT-only.

For Qwen, DeepSeek or another tool that does not discover these entry files, explicitly supply AGENTS.md and the linked shared skill/memory. Automatic support for a vendor tool is not claimed without a tested integration.
