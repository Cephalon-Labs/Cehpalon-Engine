# Experimental portable scaffolding CLI

This executable (`cehpalon`) is a bounded early slice of Engine issue #20. It
generates one real console consumer of unchanged Core, Composition and the typed
operation-registration generator. It does not generate HTTP, GraphQL, Worker,
tenant authority, providers or new runtime semantics. Full #20 and platform gates
remain outstanding; #3 is not changed by this tool.

```text
cehpalon --help
cehpalon --version
cehpalon new --name DemoApp --output <new-directory> --engine-root <existing-engine-checkout> [--profile portable-request-reply]
cehpalon validate --config <file>
```

`CliApplication.Run(string[], TextWriter, TextWriter)` returns 0 for success, 2 for
usage/configuration rejection, and 3 for path/I/O/source failure. Results are JSON;
success goes to output and diagnostics to error. Unknown or duplicate options,
extra positional arguments, unsupported profiles and names outside
`[A-Z][A-Za-z0-9]{0,63}` are rejected. DOS device names CON/PRN/AUX/NUL/COM1–9/LPT1–9
are rejected on every OS. No shells, builds, installs, downloads or services run.

`validate` uses the same bounded Composition compiler and catalog as the generated
consumer: support `core`, entrypoint `app` depending on core, and entrypoint
`optional` depending on core. Validation checks graph compatibility, not whether
application factories will succeed. Optional is a negative fixture whose consumer
factory throws if selected; no optional feature is implemented. Configuration is
UTF-8 and limited to Composition's 1 MiB bound; malformed bytes and invalid graphs
fail before activation.

`new` writes exactly nine fixed files: Consumer.csproj, Program.cs, Catalog.cs,
appsettings.json, appsettings.off.json, global.json, NuGet.Config,
engine-source.json and README.md. The supplied Engine checkout must contain the
required source inputs with the supported SDK policy and official-only NuGet
source policy. Explicit Engine paths in XML are escaped; the generated project
references Core/Composition and includes CodeGeneration as an analyzer only.
Source-root paths containing `%`, `$`, `@`, `;`, `*` or `?` are rejected because
XML escaping alone does not neutralize MSBuild expressions or item-list syntax.
Spaces, Unicode and XML-escaped ampersands do not receive this rejection.
Global/feed policy bytes are copied; no package graph is invented by generation.
The observed manifest hashes selected source inputs and states that it is not a
git attestation or complete build/input inventory. No git subprocess is invoked.

Output must be a new directory with an already-existing parent. Existing files,
directories and observed symlink/reparse-point ancestry are rejected. Known
Windows device/extended path namespaces (`\\.\` and `\\?\`) are rejected for
source, output and configuration inputs before I/O, on every host. Known
Engine/Docs/Samples checkout roots and their non-.local descendants are protected;
ignored `.local` descendants are allowed for retained test fixtures. Outputs are
deterministic for the same name, explicit source root and observed source bytes;
they do not embed target directory, time or generated GUID. Random names are used
only for private sibling staging. Fixed files are created without overwrite and
the directory is published with a non-overwriting move. Failed publication does
not delete or merge the destination. Cleanup verifies the absolute owned staging
path and deletes only expected plain files; it never recursively deletes unknown
content. Unexpected staging changes retain the stage rather than traversing it.

These checks observe paths at several points; they do not hold OS-level directory
handles or serialize arbitrary external editors. A hostile concurrent ancestor or
staging replacement can race the checks. This is a local developer tool with
trusted source/output parents, not a filesystem sandbox or cross-process atomic
source snapshot. Abrupt process loss can retain staging. Source project references
can change after generation. Future proof must check actual source/build inputs.
Generated projects inherit ordinary SDK/MSBuild parent policy; the source/output
parents and inherited build customizations must be trusted.

The generated consumer compiles config before factories, builds its generated
registry inside the enabled app factory, dispatches one echo operation, and reports
factory/handler/dispatch/disposal counters after cleanup. All-off dispatches no
work. Plain Core provides no tenant protection. Successful local generation or
build is not supported-Windows, hosted-CI, AOT/trimming, package-release or
production qualification. The internal file-operation seam is for deterministic
partial-write/publication-race contract tests, with no product test switches.
