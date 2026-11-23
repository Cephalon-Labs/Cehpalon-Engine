# Platform and provider qualification smoke

This bounded `net11.0` executable qualifies selected platform/package paths. It is
not an implementation or maturity claim for Engine Core, composition, tenancy,
business E2E, AI, performance or production hosting.

## Pins and prerequisites

- SDK `11.0.100`, with roll-forward disabled in root `global.json`.
- Runtime `11.0.0` from the same verified private SDK distribution.
- Exact direct dependency ranges in `Directory.Packages.props`; commit
  `packages.lock.json` and use `--locked-mode` on subsequent restores.
- Earlier RC SDK archive SHA512 verification and raw OS observations are in
  `evidence/`; they do not verify a GA SDK archive. No global SDK/runtime was replaced.
- The earlier RC qualification observed Ubuntu 22.04.5 LTS x64/glibc 2.35 in its
  published OS list and local Windows 11 Pro DisplayVersion 26H2/build 26300
  outside that table. Those Windows runs remain observational compatibility only.

The earlier RC qualification OS snapshot is `evidence/dotnet11-supported-os-source.md`; it remains historical evidence. Recheck the current GA support matrix and exact host before promoting a new qualification result.
The WSL Linux result is not bare-metal/container/Kubernetes performance proof.

## Reproduce using an already verified SDK

Run from the Engine checkout, selecting its verified private `dotnet` executable
and an isolated package cache/CLI home through process-scoped environment variables.
Replace `<rid>` with the execution host's `win-x64` or `linux-x64`:

```text
dotnet restore Tests/PlatformQualification/PlatformQualification.csproj --locked-mode --artifacts-path Tests/PlatformQualification/.artifacts/<rid> --configfile Tests/PlatformQualification/NuGet.Config
dotnet build Tests/PlatformQualification/PlatformQualification.csproj --configuration Release --no-restore --artifacts-path Tests/PlatformQualification/.artifacts/<rid>
dotnet Tests/PlatformQualification/.artifacts/<rid>/bin/PlatformQualification/release/PlatformQualification.dll --output=Tests/PlatformQualification/evidence/<rid>-smoke.json
```

No-server mode runs 12 platform/package checks and explicitly reports two real
provider checks as `Not run`. Exit 0 does not turn those absent checks into passes.
To require real provider qualification, inspect `notRun == 0` in the JSON result.

For explicitly owned loopback fixtures, use `run-owned-fixtures.py` with
`--dotnet`, `--assembly`, `--fixture-state` and `--output`. The private state schema
has `nodes.postgresql` and `nodes.rabbitmq` with host/port/user/password, and the
fixture database is `cehpalon_qual`. Credentials are passed through the child
process environment; the wrapper does not echo or store them in evidence.
The caller creates and later cleans up only its own provider fixtures.

## Windows Server 2025 native provider fixture

Dispatch **Platform qualification** with **windows_native_providers** enabled to
select the standard Windows Server 2025 x64 native job. Pushes, pull requests
and dispatches with the default value run the existing no-server smoke matrix.

From PowerShell 7 on a supported Windows x64 host, run from the checkout:

    ./Tests/PlatformQualification/run-windows-native-fixtures.ps1 -WorkDirectory D:/scratch/engine-platform-attempt01

Use a new ASCII scratch path for every attempt. Git, Python 3, tar.exe and
curl.exe must be available. The script checks size and publisher SHA512 or
SHA256 before extracting the fixed downloads into that scratch path. Optional
-DotnetArchive and -ProviderArchiveDirectory reuse local copies under the
same checks. The SDK and providers run from the work directory. The script
builds the locked harness and starts a fresh loopback PostgreSQL cluster and
RabbitMQ node with generated fixture credentials. It checks socket ownership
and connected version, runs the existing 14 checks and verifies shutdown.
Download or runtime failures stop qualification.

The native archive pins are .NET SDK 11.0.100, PostgreSQL 18.6 (EDB build
18.6-5), RabbitMQ 4.4.0 and Erlang/OTP 29.1.2. The provider archive bytes are
specific to Windows. The W0 Linux/amd64 OCI fixture retains its own image
digests. Keep runner/version provenance, check summaries and listener cleanup
together. The workflow uploads the credential-free evidence JSON. The
private directory contains process logs and isolated provider state for local
diagnosis. A passing run on an unlisted Windows build is observational.
The run covers platform and client-driver smoke. Full Engine provider and
benchmark qualification use their own gates.

Sources for the archive pins

- [Microsoft .NET 11 releases](https://builds.dotnet.microsoft.com/dotnet/release-metadata/11.0/releases.json)
- [EDB PostgreSQL 18.6-5 publisher checksum](https://get.enterprisedb.com/postgresql/postgresql-18.6-5-windows-x64-binaries.zip.sha256)
- [RabbitMQ 4.4.0 release](https://github.com/rabbitmq/rabbitmq-server/releases/tag/v4.4.0)
- [Erlang/OTP 29.1.2 release](https://github.com/erlang/otp/releases/tag/OTP-29.1.2)


## What the checks establish

- Exact stable runtime `11.0.0`/x64 and .NET configuration-provider precedence.
- Npgsql assembly, datasource disposal and pre-cancelled connection behavior.
- RabbitMQ client assembly and option compatibility.
- Kestrel HTTPS with an in-process test CA, explicit custom-root chain trust and
  hostname validation; wrong hostname is rejected. No trust-store install or
  certificate-validation bypass occurs. Revocation is disabled for this isolated
  short-lived CA, so this is not revocation or public-PKI proof.
- On Windows, Schannel cannot use the initially attempted ephemeral server key.
  The corrected fixture imports PFX bytes with `UserKeySet` and without
  `PersistKeySet`; .NET deletes that temporary key container on normal certificate
  disposal. Abnormal termination cleanup is not proven. No certificate is added
  to a Windows certificate trust store.
- Hot Chocolate 16.7.1 HTTP query plus disabled GET/schema/IDE exposure. This does
  not prove Engine config-driven zero activation, WebSocket/subscription behavior
  or all GraphQL security/complexity controls.
- Vendor JWT bearer checks for locally signed RS256 fixture tokens, invalid
  issuer/audience/expiry/signature and absent authentication. This is not a real
  external-IdP login, key rotation or OIDC browser test.
- Request cancellation reaches a delayed HTTP handler, followed by owned host
  shutdown.
- When fixture credentials are supplied: PostgreSQL real commit/rollback,
  parameterized query, cancellation and connection reuse; RabbitMQ durable
  single-node quorum queue with persistent publish/confirm, manual nack/requeue,
  redelivery, ack, original-channel close barrier and independent-channel empty
  queue verification before deletion. Requeue/redelivery occurs on the same
  original channel and does not prove cross-consumer ownership/failover. No broker restart/HA/outbox/exactly-once
  guarantee is established by this client smoke.

## Package provenance and licensing evidence

`collect-package-evidence.py` records selected TFM assets, archive byte hashes,
cache/lock content-hash consistency and declared license metadata. Signed raw
`.nupkg` hashes and signature-independent NuGet `contentHash` are different
quantities; the retained first checker attempt compared them incorrectly. See
[NuGet's explanation](https://github.com/NuGet/Home/wiki/Nupkg-Metadata-File).
The corrected inventory does not independently recompute the signed-content hash
or establish publisher signature trust, legal compatibility or code security.

The earlier RC inventory recorded 63 packages: 60 declare MIT, Npgsql declares PostgreSQL,
RabbitMQ.Client declares Apache-2.0 OR MPL-2.0, and ChilliCream.Nitro.App 32.0.2
uses a LICENSE file containing ChilliCream License 1.0. The latter remains in the
artifact graph even though this smoke maps no Nitro IDE. Its exact license text
and hash are retained; the whole graph must not be described as MIT-only or
distribution-cleared. Release/package distribution review remains separate.

No native NuGet runtime assets were selected in this graph. The SDK/runtime still
uses OS-native TLS/crypto/system libraries; selected-assets inventory is not proof
of absence of native dependencies in the running process.

## Evidence discipline

All initial build failures, Schannel fixture failures, CLI-option failure and the
invalid hash-checker attempt remain retained under `evidence/`. Successful final
results have distinct filenames. No performance measurement or threshold was
changed to turn a failed result into a pass.

Initial Rabbit ack checks only observed ready-message count. The later
`*-ack-barrier-owned-provider` results strengthen this oracle by closing the
original channel and checking from a new channel. Earlier outcomes and rebuild
hashes are retained separately; they do not establish this later ack barrier.

Deterministic evidence compares two fresh Release `Rebuild` outputs per OS under
the same source, SDK, lock graph and local worktree path. It is not a general
cross-path/cross-SDK reproducibility guarantee. Primary integration must recheck
the exact committed Engine revision before treating it as delivery evidence.
