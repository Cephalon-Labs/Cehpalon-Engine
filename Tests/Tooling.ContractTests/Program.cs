using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cehpalon.Engine.Tooling;

var checks = new List<object>();
var failures = 0;
var runRoot = Path.Combine(Path.GetTempPath(), "cehpalon-tooling-contracts-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(runRoot);
var engineRoot = FindEngineRoot();
var ordinal = 0;
string[] expectedFiles = ["Catalog.cs", "Consumer.csproj", "NuGet.Config", "Program.cs", "README.md", "appsettings.json", "appsettings.off.json", "engine-source.json", "global.json"];
string[] sourcePaths =
[
    "Directory.Packages.props", "NuGet.Config", "global.json",
    "src/Cehpalon.Engine.Core/Cehpalon.Engine.Core.csproj", "src/Cehpalon.Engine.Core/Contracts.cs",
    "src/Cehpalon.Engine.Core/OperationRegistry.cs", "src/Cehpalon.Engine.Core/OperationResult.cs", "src/Cehpalon.Engine.Core/packages.lock.json",
    "src/Cehpalon.Engine.Composition/Cehpalon.Engine.Composition.csproj", "src/Cehpalon.Engine.Composition/CompositionCompiler.cs",
    "src/Cehpalon.Engine.Composition/Contracts.cs", "src/Cehpalon.Engine.Composition/PlanActivator.cs", "src/Cehpalon.Engine.Composition/packages.lock.json",
    "src/Cehpalon.Engine.CodeGeneration/Cehpalon.Engine.CodeGeneration.csproj", "src/Cehpalon.Engine.CodeGeneration/NuGet.Config",
    "src/Cehpalon.Engine.CodeGeneration/OperationRegistryGenerator.cs", "src/Cehpalon.Engine.CodeGeneration/packages.lock.json"
];

void Assert(bool condition, string message = "Assertion failed")
{
    if (!condition) throw new InvalidOperationException(message);
}

void Check(string name, Action test)
{
    try { test(); checks.Add(new { name, passed = true }); }
    catch (Exception error) { failures++; checks.Add(new { name, passed = false, detail = error.ToString() }); }
}

string Scope()
{
    var path = Path.Combine(runRoot, (++ordinal).ToString("D3"));
    Directory.CreateDirectory(path);
    return path;
}

CliResult Invoke(string[] arguments, IToolingFileSystem? fileSystem = null)
{
    using var output = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
    using var error = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
    var code = fileSystem is null ? CliApplication.Run(arguments, output, error) : CliApplication.Run(arguments, output, error, fileSystem);
    return new(code, output.ToString(), error.ToString());
}
CliResult Run(params string[] arguments) => Invoke(arguments);

void AssertDiagnostic(CliResult result, string code, string? redacted = null, string? nestedCode = null)
{
    Assert(result.Code == 2, "Expected validation exit 2.");
    Assert(result.Output.Length == 0, "Validation errors must not write to stdout.");
    if (redacted is not null)
        Assert(!result.Error.Contains(redacted, StringComparison.Ordinal), "Diagnostic echoed rejected input.");
    using var json = JsonDocument.Parse(result.Error);
    var error = json.RootElement;
    Assert(error.GetProperty("success").ValueKind == JsonValueKind.False);
    Assert(error.GetProperty("code").GetString() == code, "Unexpected diagnostic code; expected " + code);
    Assert(!string.IsNullOrWhiteSpace(error.GetProperty("message").GetString()));
    if (nestedCode is not null)
    {
        var diagnostics = error.GetProperty("diagnostics");
        Assert(diagnostics.EnumerateArray().Any(diagnostic => diagnostic.GetProperty("code").GetString() == nestedCode));
        Assert(diagnostics.EnumerateArray().All(diagnostic => diagnostic.GetProperty("path").GetString() == "$.Cehpalon"
            && !string.IsNullOrWhiteSpace(diagnostic.GetProperty("message").GetString())));
    }
}

string[] NewArguments(string target, string name = "Demo", string? root = null)
    => ["new", "--name", name, "--output", target, "--engine-root", root ?? engineRoot];

Dictionary<string, string> Snapshot(string root)
    => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
        .ToDictionary(path => Path.GetRelativePath(root, path).Replace('\\', '/'),
            path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))), StringComparer.Ordinal);

void SameSnapshot(Dictionary<string, string> before, string root)
{
    var after = Snapshot(root);
    Assert(before.Count == after.Count && before.All(pair => after.TryGetValue(pair.Key, out var hash) && hash == pair.Value),
        "Unexpected file creation, deletion or content change.");
}

void AssertNoPublish(string parent, string target)
{
    Assert(!Directory.Exists(target) && !File.Exists(target), "A rejected request published a target.");
    Assert(!Directory.EnumerateFileSystemEntries(parent).Any(), "A rejected request left owned files or staging directories.");
}

string CreateSourceFixture(string parent)
{
    var root = Path.Combine(parent, "Cehpalon-Engine");
    Directory.CreateDirectory(Path.Combine(root, ".git"));
    Directory.CreateDirectory(Path.Combine(parent, "Cehpalon-Engine-Docs", ".git"));
    Directory.CreateDirectory(Path.Combine(parent, "Cehpalon-Engine-Samples", ".git"));
    foreach (var relative in sourcePaths)
    {
        var target = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(Path.Combine(engineRoot, relative.Replace('/', Path.DirectorySeparatorChar)), target);
    }
    return root;
}

void UsageWithoutFiles(params string[] arguments)
{
    var parent = Scope();
    var target = Path.Combine(parent, "output");
    var expanded = arguments.Select(value => value == "$output" ? target : value == "$engine" ? engineRoot : value).ToArray();
    var result = Run(expanded);
    Assert(result.Code == 2, "Expected usage/config exit 2; got " + result.Code);
    Assert(!string.IsNullOrWhiteSpace(result.Error), "Rejected usage must provide a diagnostic.");
    AssertNoPublish(parent, target);
}

Check("help-without-filesystem-work", () =>
{
    var before = Snapshot(runRoot);
    var result = Run("--help");
    Assert(result.Code == 0 && result.Output.Contains("new", StringComparison.Ordinal) && result.Output.Contains("validate", StringComparison.Ordinal));
    Assert(result.Error.Length == 0);
    SameSnapshot(before, runRoot);
});
Check("version-without-filesystem-work", () =>
{
    var before = Snapshot(runRoot);
    var result = Run("--version");
    Assert(result.Code == 0 && !string.IsNullOrWhiteSpace(result.Output) && result.Error.Length == 0);
    SameSnapshot(before, runRoot);
});

Check("no-command-shows-help-without-filesystem-work", () =>
{
    var before = Snapshot(runRoot);
    var result = Run();
    Assert(result.Code == 0 && result.Error.Length == 0 && result.Output.Contains("new", StringComparison.Ordinal));
    SameSnapshot(before, runRoot);
});

JsonDocument ReadCommandHelp(string command)
{
    var writes = 0;
    var moves = 0;
    var fileSystem = new DelegatingFileSystem((_, _) => writes++, (_, _) => moves++);
    var result = Invoke([command, "--help"], fileSystem);
    Assert(writes == 0 && moves == 0, "Command help must not write or publish through the filesystem seam.");
    Assert(result.Code == 0 && result.Error.Length == 0, "Expected exit 0 and empty stderr for command help; got " + result.Code);
    Assert(result.Output.EndsWith(Environment.NewLine, StringComparison.Ordinal)
        && result.Output[..^Environment.NewLine.Length].IndexOfAny(['\r', '\n']) < 0,
        "Expected exactly one JSON stdout line.");
    return JsonDocument.Parse(result.Output);
}

void AssertHelpHeader(JsonElement help, string command, string usage, string example)
{
    Assert(help.GetProperty("success").GetBoolean());
    Assert(help.GetProperty("command").GetString() == command);
    Assert(!string.IsNullOrWhiteSpace(help.GetProperty("description").GetString()));
    Assert(help.GetProperty("usage").GetString() == usage);
    Assert(help.GetProperty("profiles").EnumerateArray().Select(profile => profile.GetString())
        .SequenceEqual(["portable-request-reply", "small-api"]));
    Assert(help.GetProperty("defaultProfile").GetString() == "portable-request-reply");
    Assert(help.GetProperty("example").GetString() == example);
}

Dictionary<string, string> ReadHelpOptions(JsonElement help, params string[] requiredOptions)
{
    var options = help.GetProperty("options").EnumerateArray().ToDictionary(option => option.GetProperty("name").GetString()!);
    Assert(options.Count == requiredOptions.Length + 1, "Help must list only this command's options.");
    Assert(!options["--profile"].GetProperty("required").GetBoolean());
    foreach (var name in requiredOptions)
        Assert(options[name].GetProperty("required").GetBoolean(), "Help must mark " + name + " as required.");
    return options.ToDictionary(option => option.Key, option => option.Value.GetProperty("description").GetString()!);
}

Check("new-command-help-lists-scaffold-options-and-constraints", () =>
{
    using var json = ReadCommandHelp("new");
    var help = json.RootElement;
    AssertHelpHeader(help, "new",
        "cehpalon new --name <ASCIIUpperIdentifier> --output <new-directory> --engine-root <existing-checkout> [--profile portable-request-reply|small-api]",
        "cehpalon new --name DemoApp --output ./DemoApp --engine-root ./Cehpalon-Engine");
    var options = ReadHelpOptions(help, "--name", "--output", "--engine-root");
    Assert(options["--name"].Contains("[A-Z][A-Za-z0-9]{0,63}", StringComparison.Ordinal)
        && options["--name"].Contains("DOS device names", StringComparison.Ordinal));
    Assert(options["--output"].Contains("existing parent", StringComparison.Ordinal)
        && options["--output"].Contains("no overwrite", StringComparison.Ordinal)
        && options["--output"].Contains(".local", StringComparison.Ordinal));
    Assert(options["--engine-root"].Contains("existing Engine checkout", StringComparison.Ordinal)
        && options["--engine-root"].Contains("required source inputs", StringComparison.Ordinal)
        && options["--engine-root"].Contains("MSBuild", StringComparison.Ordinal));
});

Check("validate-command-help-lists-config-policy-and-no-activation", () =>
{
    using var json = ReadCommandHelp("validate");
    var help = json.RootElement;
    AssertHelpHeader(help, "validate", "cehpalon validate --config <file> [--profile portable-request-reply|small-api]",
        "cehpalon validate --config ./DemoApp/appsettings.json");
    Assert(help.GetProperty("description").GetString()!.Contains("without activating", StringComparison.Ordinal));
    var options = ReadHelpOptions(help, "--config");
    Assert(options["--config"].Contains("JSON file", StringComparison.Ordinal)
        && options["--config"].Contains("strict UTF-8", StringComparison.Ordinal)
        && options["--config"].Contains("1048576 bytes", StringComparison.Ordinal)
        && options["--config"].Contains("BOM", StringComparison.Ordinal));
});

const string rejectedHelpInput = "rejected-command-help-input-fixture";
foreach (var invocation in new (string Name, string Code, string[] Arguments)[]
{
    ("new-help-extra-argument", "usage.option", ["new", "--help", rejectedHelpInput]),
    ("validate-help-extra-argument", "usage.option", ["validate", "--help", rejectedHelpInput]),
    ("validate-help-as-config-value", "usage.value", ["validate", "--config", "--help"]),
    ("new-help-as-output-value", "usage.value", ["new", "--output", "--help"]),
    ("validate-help-after-config-path", "usage.option", ["validate", "--config", "./" + rejectedHelpInput + ".json", "--help"]),
    ("new-help-command-is-case-sensitive", "usage.command", ["New", "--help"]),
    ("validate-help-option-is-case-sensitive", "usage.option", ["validate", "--HELP"])
})
    Check(invocation.Name, () => AssertDiagnostic(Run(invocation.Arguments), invocation.Code, rejectedHelpInput));

const string requiredInput = "RequiredOptionInputFixture";
const string optionError = "Unknown option; use --help for supported arguments.";
const string profileError = "Supported profiles are portable-request-reply and small-api.";
const string configValueError = "Option --config requires one nonempty value; use cehpalon validate --help.";
const string outputValueError = "Option --output requires one nonempty value; use cehpalon new --help.";
const string validateProfileValueError = "Option --profile requires one nonempty value; use cehpalon validate --help.";
const string nextOption = "--unrecognized-" + requiredInput;
var requiredOptionDiagnostics = new (string Name, string[] Arguments, int Exit, string Code, string Message)[]
{
    ("new-missing-name", ["new", "--output", "$target", "--engine-root", "$source"],
        2, "usage.required", "Missing required option --name; use cehpalon new --help."),
    ("new-missing-output", ["new", "--engine-root", "$source", "--name", requiredInput],
        2, "usage.required", "Missing required option --output; use cehpalon new --help."),
    ("new-missing-engine-root-before-output-parent-check", ["new", "--name", requiredInput, "--output", "$nested"],
        2, "usage.required", "Missing required option --engine-root; use cehpalon new --help."),
    ("validate-missing-config", ["validate", "--profile", "small-api"],
        2, "usage.required", "Missing required option --config; use cehpalon validate --help."),
    ("new-missing-name-before-profile-and-other-missing-options", ["new", "--profile", requiredInput],
        2, "usage.required", "Missing required option --name; use cehpalon new --help."),
    ("new-missing-output-before-missing-engine-root", ["new", "--name", requiredInput],
        2, "usage.required", "Missing required option --output; use cehpalon new --help."),
    ("new-invalid-name-before-profile-and-output", ["new", "--name", "invalid-" + requiredInput, "--profile", requiredInput],
        2, "usage.name", "Use [A-Z][A-Za-z0-9]{0,63}, excluding reserved DOS device names."),
    ("new-invalid-profile-before-output", ["new", "--name", requiredInput, "--profile", requiredInput],
        2, "usage.profile", profileError),
    ("new-output-namespace-before-missing-engine-root", ["new", "--name", requiredInput, "--output", @"\\?\C:\" + requiredInput],
        3, "path.namespace", "Windows device and extended path namespaces are not supported."),
    ("validate-invalid-profile-before-missing-config", ["validate", "--profile", requiredInput],
        2, "usage.profile", profileError),
    ("new-malformed-output-before-missing-name", ["new", "--output", " "], 2, "usage.value", outputValueError),
    ("validate-malformed-config-is-not-missing", ["validate", "--config"], 2, "usage.value", configValueError),
    ("new-duplicate-before-missing-name", ["new", "--output", "$target", "--output", "$nested"],
        2, "usage.duplicate", "Option --output must not be repeated; use cehpalon new --help."),
    ("new-mixed-help-before-missing-output", ["new", "--name", requiredInput, "--help"], 2, "usage.option", optionError),
    ("validate-mixed-help-before-missing-config", ["validate", "--profile", "small-api", "--help"], 2, "usage.option", optionError),
    ("validate-unknown-argument-before-missing-config", ["validate", "--" + requiredInput, "value"], 2, "usage.option", optionError)
};
var optionValueDiagnostics = new (string Name, string[] Arguments, int Exit, string Code, string Message)[]
{
    ("option-value-validate-profile-before-next-option-and-config-read", ["validate", "--config", "$config", "--profile", nextOption],
        2, "usage.value", validateProfileValueError),
    ("option-value-validate-config-followed-by-help-is-not-command-help", ["validate", "--config", "--help"],
        2, "usage.value", configValueError),
    ("option-value-new-empty-name", ["new", "--name", "", "--output", "$target", "--engine-root", "$source"],
        2, "usage.value", "Option --name requires one nonempty value; use cehpalon new --help."),
    ("option-value-new-missing-engine-root", ["new", "--name", requiredInput, "--output", "$target", "--engine-root"],
        2, "usage.value", "Option --engine-root requires one nonempty value; use cehpalon new --help."),
    ("option-value-new-profile-before-next-option-and-source-read", ["new", "--name", requiredInput, "--output", "$target", "--engine-root", "$source", "--profile", nextOption],
        2, "usage.value", "Option --profile requires one nonempty value; use cehpalon new --help."),
    ("option-value-validate-malformed-repeat-before-duplicate-and-config-read", ["validate", "--config", "$config", "--config"],
        2, "usage.value", configValueError)
};
var duplicateOptionDiagnostics = new (string Name, string[] Arguments, int Exit, string Code, string Message)[]
{
    ("duplicate-option-new-name", ["new", "--name", "First" + requiredInput, "--output", "$target", "--engine-root", "$engine", "--name", "Second" + requiredInput],
        2, "usage.duplicate", "Option --name must not be repeated; use cehpalon new --help."),
    ("duplicate-option-new-engine-root", ["new", "--name", "Demo", "--output", "$target", "--engine-root", "$source", "--engine-root", "$nested"],
        2, "usage.duplicate", "Option --engine-root must not be repeated; use cehpalon new --help."),
    ("duplicate-option-new-profile", ["new", "--name", "Demo", "--output", "$target", "--engine-root", "$source", "--profile", "portable-request-reply", "--profile", "small-api"],
        2, "usage.duplicate", "Option --profile must not be repeated; use cehpalon new --help."),
    ("duplicate-option-validate-config", ["validate", "--config", "$config", "--config", "$nested"],
        2, "usage.duplicate", "Option --config must not be repeated; use cehpalon validate --help."),
    ("duplicate-option-validate-profile", ["validate", "--config", "$config", "--profile", "small-api", "--profile", "portable-request-reply"],
        2, "usage.duplicate", "Option --profile must not be repeated; use cehpalon validate --help."),
    ("duplicate-option-new-blank-second-value", ["new", "--output", "$target", "--output", " \t"],
        2, "usage.value", outputValueError),
    ("duplicate-option-validate-option-like-second-value", ["validate", "--config", "$config", "--config", nextOption],
        2, "usage.value", configValueError),
    ("duplicate-option-validate-earlier-unknown-stays-unknown", ["validate", "--config", "$config", nextOption, requiredInput, "--config", "$nested"],
        2, "usage.option", optionError),
    ("duplicate-option-new-case-variant-is-unknown", ["new", "--output", "$target", "--Output", "$nested"],
        2, "usage.option", optionError)
};
foreach (var scenario in requiredOptionDiagnostics
    .Select(test => (Name: "required-option-" + test.Name, test.Arguments, test.Exit, test.Code, test.Message))
    .Concat(optionValueDiagnostics).Concat(duplicateOptionDiagnostics))
{
    Check(scenario.Name, () =>
    {
        var parent = Scope();
        var target = Path.Combine(parent, requiredInput);
        var arguments = scenario.Arguments.Select(value => value switch
        {
            "$target" => target,
            "$nested" => Path.Combine(target, "nested"),
            "$source" => Path.Combine(parent, requiredInput + "Source"),
            "$engine" => engineRoot,
            "$config" => Path.Combine(parent, requiredInput + ".json"),
            _ => value
        }).ToArray();
        var writes = 0;
        var moves = 0;
        var fileSystem = new DelegatingFileSystem((_, _) => writes++, (_, _) => moves++);
        var result = Invoke(arguments, fileSystem);
        Assert(writes == 0 && moves == 0, "Rejected usage must not write or publish through the filesystem seam.");
        AssertNoPublish(parent, target);
        Assert(result.Code == scenario.Exit && result.Output.Length == 0, "Expected rejection with no stdout and exit " + scenario.Exit);
        Assert(!result.Error.Contains(requiredInput, StringComparison.Ordinal), "Diagnostic echoed user-supplied input.");
        if (scenario.Code is "usage.value" or "usage.duplicate")
            AssertDiagnostic(result, scenario.Code);
        var expected = $$"""{"success":false,"code":"{{scenario.Code}}","message":"{{scenario.Message}}"}""";
        Assert(result.Error == expected + Environment.NewLine, "Expected exactly one stderr JSON line: " + expected);
    });
}

foreach (var invocation in new (string Name, string[] Arguments)[]
{
    ("unknown-command", ["launch"]),
    ("help-extra-argument", ["--help", "extra"]),
    ("version-extra-argument", ["--version", "extra"]),
    ("new-missing-options", ["new"]),
    ("new-missing-output-value", ["new", "--name", "Demo", "--output"]),
    ("new-missing-engine-root", ["new", "--name", "Demo", "--output", "$output"]),
    ("new-unknown-option", ["new", "--name", "Demo", "--output", "$output", "--engine-root", "$engine", "--start", "true"]),
    ("new-duplicate-name", ["new", "--name", "Demo", "--name", "Other", "--output", "$output", "--engine-root", "$engine"]),
    ("new-duplicate-output", ["new", "--name", "Demo", "--output", "$output", "--output", "$output", "--engine-root", "$engine"]),
    ("new-duplicate-engine-root", ["new", "--name", "Demo", "--output", "$output", "--engine-root", "$engine", "--engine-root", "$engine"]),
    ("new-duplicate-profile", ["new", "--name", "Demo", "--output", "$output", "--engine-root", "$engine", "--profile", "portable-request-reply", "--profile", "portable-request-reply"]),
    ("new-unsupported-profile", ["new", "--name", "Demo", "--output", "$output", "--engine-root", "$engine", "--profile", "durable-distributed"]),
    ("new-positional-trailer", ["new", "--name", "Demo", "--output", "$output", "--engine-root", "$engine", "extra"]),
    ("validate-missing-config", ["validate"]),
    ("validate-missing-value", ["validate", "--config"]),
    ("validate-unknown-option", ["validate", "--config", "$output", "--unknown", "value"]),
    ("validate-duplicate-config", ["validate", "--config", "$output", "--config", "$output"])
})
{
    Check(invocation.Name, () => UsageWithoutFiles(invocation.Arguments));
}

foreach (var scenario in new (string Name, string Code, string[] ProfileArguments)[]
{
    ("unsupported", "usage.profile", ["--profile", "rejected-profile-fixture"]),
    ("duplicate", "usage.duplicate", ["--profile", "small-api", "--profile", "rejected-profile-fixture"]),
    ("missing-value", "usage.value", ["--profile"])
})
{
    Check("validate-profile-diagnostic-" + scenario.Name, () =>
    {
        var parent = Scope();
        var config = Path.Combine(parent, "missing.json");
        const string rejectedProfile = "rejected-profile-fixture";
        AssertDiagnostic(Run(["validate", "--config", config, .. scenario.ProfileArguments]), scenario.Code,
            scenario.ProfileArguments.Contains(rejectedProfile, StringComparer.Ordinal) ? rejectedProfile : null);
        AssertNoPublish(parent, config);
    });
}

foreach (var name in new[] { "", "lower", "0Demo", "With-Dash", "With.Space", "With Space", "../Escape", "A/B", "A\\B", "ไทย", "Demo\nInjected", "CON", "PRN", "AUX", "NUL", "LPT9", "Com1", new string('A', 65) })
{
    var description = name.Length > 20 ? "length-" + name.Length : name.Replace("\n", "\\n", StringComparison.Ordinal);
    Check("invalid-name-before-publish-" + description, () =>
    {
        var parent = Scope();
        var target = Path.Combine(parent, "output");
        var result = Run(NewArguments(target, name));
        Assert(result.Code == 2, "Invalid name should be usage error, got " + result.Code);
        AssertNoPublish(parent, target);
    });
}

Check("maximum-name-length-supported", () =>
{
    var parent = Scope();
    var target = Path.Combine(parent, "output");
    var result = Run(NewArguments(target, new string('A', 64)));
    Assert(result.Code == 0 && Directory.Exists(target), "64-character name was rejected: " + result.Error);
});

Check("scaffold-does-not-run-build-or-services", () =>
{
    var parent = Scope();
    var target = Path.Combine(parent, "output");
    var result = Run(NewArguments(target));
    Assert(result.Code == 0 && result.Error.Length == 0, "Scaffold failed: " + result.Error);
    var files = Snapshot(target);
    Assert(files.Keys.Order(StringComparer.Ordinal).SequenceEqual(expectedFiles.Order(StringComparer.Ordinal)),
        "Generated file set differs from the bounded nine-file contract: " + string.Join(",", files.Keys));
    Assert(!Directory.EnumerateDirectories(target, "*", SearchOption.AllDirectories).Any(path =>
        new[] { "bin", "obj", ".git", "node_modules" }.Contains(Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)),
        "Generation must not build, restore or initialize other tools.");
    foreach (var path in Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories))
    {
        var bytes = File.ReadAllBytes(path);
        Assert(bytes.Length > 0, "Empty generated file.");
        Assert(!(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF), "UTF8 BOM is not deterministic template encoding.");
        _ = new UTF8Encoding(false, true).GetString(bytes);
    }
});

Check("same-options-generate-identical-relative-file-bytes", () =>
{
    var parent = Scope();
    var first = Path.Combine(parent, "first");
    var second = Path.Combine(parent, "second");
    Assert(Run(NewArguments(first)).Code == 0);
    Assert(Run([.. NewArguments(second), "--profile", "portable-request-reply"]).Code == 0);
    var expected = Snapshot(first);
    SameSnapshot(expected, second);
});

foreach (var failAfterWrite in new[] { false, true })
{
    for (var writeNumber = 1; writeNumber <= expectedFiles.Length; writeNumber++)
    {
        var failedWrite = writeNumber;
        Check($"write-failure-{failedWrite}-{(failAfterWrite ? "after" : "before")}-cleans-only-owned-stage", () =>
        {
            var parent = Scope();
            var target = Path.Combine(parent, "output");
            var neighbor = Path.Combine(parent, "neighbor.bin");
            File.WriteAllBytes(neighbor, [0, 255, 10, 13, 71]);
            var before = Snapshot(parent);
            var physical = new PhysicalToolingFileSystem();
            var calls = 0;
            var moves = 0;
            var fileSystem = new DelegatingFileSystem(
                (path, bytes) =>
                {
                    calls++;
                    if (calls == failedWrite && !failAfterWrite) throw new IOException("SUPER_SECRET_IO_MESSAGE");
                    physical.WriteAllBytes(path, bytes);
                    if (calls == failedWrite && failAfterWrite) throw new IOException("SUPER_SECRET_IO_MESSAGE");
                },
                (source, destination) => { moves++; physical.MoveDirectory(source, destination); });
            var result = Invoke(NewArguments(target), fileSystem);
            Assert(result.Code == 3, "Injected I/O failure must return exit3.");
            Assert(calls == failedWrite && moves == 0, "Generation continued or published after write failure.");
            Assert(!(result.Output + result.Error).Contains("SUPER_SECRET_IO_MESSAGE", StringComparison.Ordinal), "Unexpected filesystem details leaked.");
            SameSnapshot(before, parent);
            Assert(Directory.EnumerateFileSystemEntries(parent).Count() == 1, "Owned staging directory survived write failure.");
        });
    }
}

Check("publication-collision-preserves-outsider-and-cleans-owned-stage", () =>
{
    var parent = Scope();
    var target = Path.Combine(parent, "output");
    var physical = new PhysicalToolingFileSystem();
    var writes = 0;
    var moves = 0;
    string? staging = null;
    var fileSystem = new DelegatingFileSystem(
        (path, bytes) => { writes++; physical.WriteAllBytes(path, bytes); },
        (source, destination) =>
        {
            moves++;
            staging = source;
            Assert(Snapshot(source).Count == expectedFiles.Length, "Publication attempted before full template was written.");
            Directory.CreateDirectory(destination);
            File.WriteAllBytes(Path.Combine(destination, "outsider.bin"), [1, 255, 13, 10, 0]);
            physical.MoveDirectory(source, destination);
        });
    var result = Invoke(NewArguments(target), fileSystem);
    Assert(result.Code == 3 && writes == expectedFiles.Length && moves == 1);
    Assert(staging is not null && !Directory.Exists(staging), "Owned staging was retained after collision.");
    Assert(File.ReadAllBytes(Path.Combine(target, "outsider.bin")).SequenceEqual(new byte[] { 1, 255, 13, 10, 0 }), "Outsider target was damaged.");
    Assert(Directory.EnumerateFileSystemEntries(target).Count() == 1 && Directory.EnumerateFileSystemEntries(parent).Count() == 1,
        "Collision cleanup removed the outsider or retained generated files.");
});

Check("publication-io-failure-cleans-owned-stage-without-publishing", () =>
{
    var parent = Scope();
    var target = Path.Combine(parent, "output");
    var physical = new PhysicalToolingFileSystem();
    var writes = 0;
    var fileSystem = new DelegatingFileSystem(
        (path, bytes) => { writes++; physical.WriteAllBytes(path, bytes); },
        (_, _) => throw new IOException("SUPER_SECRET_IO_MESSAGE"));
    var result = Invoke(NewArguments(target), fileSystem);
    Assert(result.Code == 3 && writes == expectedFiles.Length);
    Assert(!(result.Output + result.Error).Contains("SUPER_SECRET_IO_MESSAGE", StringComparison.Ordinal));
    AssertNoPublish(parent, target);
});

Check("unexpected-staging-entry-retains-stage-and-outsider-bytes", () =>
{
    var parent = Scope();
    var target = Path.Combine(parent, "output");
    var physical = new PhysicalToolingFileSystem();
    string? stage = null;
    string? producedFile = null;
    byte[]? producedBytes = null;
    var fileSystem = new DelegatingFileSystem(
        (path, bytes) =>
        {
            physical.WriteAllBytes(path, bytes);
            stage = Path.GetDirectoryName(path)!;
            producedFile = path;
            producedBytes = bytes;
            File.WriteAllBytes(Path.Combine(stage, "outsider.bin"), [0, 255, 13, 10, 9]);
            throw new IOException("SUPER_SECRET_IO_MESSAGE");
        },
        (_, _) => throw new InvalidOperationException("Publication must not run after write failure."));
    var result = Invoke(NewArguments(target), fileSystem);
    Assert(result.Code == 3 && stage is not null && Directory.Exists(stage));
    Assert(!Directory.Exists(target));
    Assert(File.ReadAllBytes(Path.Combine(stage!, "outsider.bin")).SequenceEqual(new byte[] { 0, 255, 13, 10, 9 }));
    Assert(File.ReadAllBytes(producedFile!).SequenceEqual(producedBytes!), "Unsafe cleanup partially deleted known files before discovering outsider.");
    Assert(Directory.EnumerateFileSystemEntries(stage!).Count() == 2 && Directory.EnumerateFileSystemEntries(parent).Count() == 1);
    Assert(!(result.Output + result.Error).Contains("SUPER_SECRET_IO_MESSAGE", StringComparison.Ordinal));
    using var error = JsonDocument.Parse(result.Error);
    Assert(error.RootElement.GetProperty("code").GetString() == "path.cleanup", "Unsafe cleanup must be distinct from successful rollback.");
});

Check("preexisting-directory-and-sentinel-remain-unchanged", () =>
{
    var parent = Scope();
    var target = Path.Combine(parent, "output");
    Directory.CreateDirectory(target);
    File.WriteAllBytes(Path.Combine(target, "sentinel.bin"), [0, 255, 10, 13, 1]);
    var before = Snapshot(parent);
    var result = Run(NewArguments(target));
    Assert(result.Code == 3);
    SameSnapshot(before, parent);
    Assert(Directory.EnumerateDirectories(parent).Count() == 1);
});

Check("preexisting-empty-directory-is-not-adopted", () =>
{
    var parent = Scope();
    var target = Path.Combine(parent, "output");
    Directory.CreateDirectory(target);
    Assert(Run(NewArguments(target)).Code == 3);
    Assert(Directory.Exists(target) && !Directory.EnumerateFileSystemEntries(target).Any());
    Assert(Directory.EnumerateFileSystemEntries(parent).Count() == 1);
});

Check("preexisting-file-is-not-overwritten", () =>
{
    var parent = Scope();
    var target = Path.Combine(parent, "output");
    File.WriteAllText(target, "ORIGINAL\r\nBYTES", new UTF8Encoding(false));
    var before = Snapshot(parent);
    Assert(Run(NewArguments(target)).Code == 3);
    SameSnapshot(before, parent);
});

Check("missing-parent-is-not-created", () =>
{
    var parent = Scope();
    var target = Path.Combine(parent, "absent", "output");
    Assert(Run(NewArguments(target)).Code == 3);
    AssertNoPublish(parent, target);
});

Check("missing-engine-root-is-not-created", () =>
{
    var parent = Scope();
    var target = Path.Combine(parent, "output");
    Assert(Run(NewArguments(target, root: Path.Combine(parent, "absent-engine"))).Code == 3);
    AssertNoPublish(parent, target);
});

Check("engine-root-file-is-not-a-project-root", () =>
{
    var parent = Scope();
    var target = Path.Combine(parent, "output");
    var file = Path.Combine(parent, "engine-file");
    File.WriteAllText(file, "sentinel");
    var before = Snapshot(parent);
    Assert(Run(NewArguments(target, root: file)).Code == 3);
    SameSnapshot(before, parent);
});

Check("empty-engine-directory-is-not-accepted", () =>
{
    var parent = Scope();
    var target = Path.Combine(parent, "output");
    var empty = Path.Combine(parent, "empty-engine");
    Directory.CreateDirectory(empty);
    Assert(Run(NewArguments(target, root: empty)).Code == 3);
    Assert(!Directory.Exists(target) && !Directory.EnumerateFileSystemEntries(empty).Any());
    Assert(Directory.EnumerateFileSystemEntries(parent).Count() == 1);
});

Check("new-output-under-engine-src-rejected-without-any-sibling-change", () =>
{
    var parent = Path.Combine(engineRoot, "src");
    var before = Directory.EnumerateFileSystemEntries(parent).Order(StringComparer.Ordinal).ToArray();
    var target = Path.Combine(parent, "ToolingShouldNeverCreate" + Guid.NewGuid().ToString("N"));
    var result = Run(NewArguments(target));
    Assert(result.Code == 3, "An unignored checkout subtree must not be scaffold output.");
    Assert(!Directory.Exists(target) && !File.Exists(target));
    Assert(before.SequenceEqual(Directory.EnumerateFileSystemEntries(parent).Order(StringComparer.Ordinal)), "Rejected output altered the checkout subtree.");
});

Check("new-output-under-own-existing-local-parent-is-allowed", () =>
{
    var parent = Path.Combine(engineRoot, ".local", "tooling-contracts-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(parent);
    var target = Path.Combine(parent, "output");
    var result = Run(NewArguments(target));
    Assert(result.Code == 0, "Ignored .local output should be allowed: " + result.Error);
    Assert(Snapshot(target).Count == expectedFiles.Length);
});

Check("same-named-runner-wrapper-is-not-a-checkout-boundary", () =>
{
    var wrapper = Path.Combine(Scope(), "Cehpalon-Engine");
    Directory.CreateDirectory(wrapper);
    var source = CreateSourceFixture(wrapper);
    var parent = Path.Combine(source, ".local");
    Directory.CreateDirectory(parent);
    var target = Path.Combine(parent, "consumer");
    var result = Run(NewArguments(target, root: source));
    Assert(result.Code == 0, "A same-named wrapper without .git must not hide the actual checkout's .local: " + result.Error);
    Assert(Snapshot(target).Count == expectedFiles.Length);
    Assert(!Directory.Exists(Path.Combine(wrapper, ".git")), "The wrapper must remain an ordinary parent directory.");
});

foreach (var checkoutName in new[] { "Cehpalon-Engine", "Cehpalon-Engine-Docs", "Cehpalon-Engine-Samples" })
{
    Check("canonical-checkout-unignored-descendant-is-not-output-" + checkoutName, () =>
    {
        var programRoot = Scope();
        var source = CreateSourceFixture(programRoot);
        var parent = Path.Combine(programRoot, checkoutName);
        var before = Directory.EnumerateFileSystemEntries(parent).Order(StringComparer.Ordinal).ToArray();
        var target = Path.Combine(parent, "ToolingShouldNeverCreate" + Guid.NewGuid().ToString("N"));
        Assert(Run(NewArguments(target, root: source)).Code == 3);
        Assert(!Directory.Exists(target) && !File.Exists(target));
        Assert(before.SequenceEqual(Directory.EnumerateFileSystemEntries(parent).Order(StringComparer.Ordinal)), "Rejected output altered a sibling checkout.");
    });
}

foreach (var invalidSource in new (string Name, string Path, string Text)[]
{
    ("malformed-sdk-json", "global.json", "{ broken SUPER_SECRET_SOURCE_VALUE"),
    ("scalar-sdk-policy", "global.json", "{\"sdk\":\"SUPER_SECRET_SOURCE_VALUE\"}"),
    ("nonobject-sdk-root", "global.json", "[\"SUPER_SECRET_SOURCE_VALUE\"]"),
    ("numeric-sdk-version", "global.json", "{\"sdk\":{\"version\":123,\"rollForward\":\"disable\",\"allowPrerelease\":false}}"),
    ("wrong-sdk-version", "global.json", "{\"sdk\":{\"version\":\"SUPER_SECRET_SOURCE_VALUE\",\"rollForward\":\"disable\",\"allowPrerelease\":false}}"),
    ("release-candidate-sdk", "global.json", "{\"sdk\":{\"version\":\"11.0.100-rc.3.26522.148\",\"rollForward\":\"disable\",\"allowPrerelease\":false}}"),
    ("ga-sdk-prerelease-enabled", "global.json", "{\"sdk\":{\"version\":\"11.0.100\",\"rollForward\":\"disable\",\"allowPrerelease\":true}}"),
    ("ga-sdk-prerelease-unspecified", "global.json", "{\"sdk\":{\"version\":\"11.0.100\",\"rollForward\":\"disable\"}}"),
    ("ga-sdk-roll-forward-enabled", "global.json", "{\"sdk\":{\"version\":\"11.0.100\",\"rollForward\":\"latestPatch\",\"allowPrerelease\":false}}"),
    ("malformed-package-xml", "NuGet.Config", "<configuration>SUPER_SECRET_SOURCE_VALUE"),
    ("unapproved-package-feed", "NuGet.Config", "<configuration><packageSources><clear/><add key=\"secret\" value=\"https://SUPER_SECRET_SOURCE_VALUE.invalid/feed\" protocolVersion=\"3\"/></packageSources></configuration>")
})
{
    Check("invalid-source-" + invalidSource.Name + "-redacted-and-before-output", () =>
    {
        var parent = Scope();
        var source = CreateSourceFixture(parent);
        File.WriteAllText(Path.Combine(source, invalidSource.Path), invalidSource.Text, new UTF8Encoding(false));
        var before = Snapshot(parent);
        var target = Path.Combine(parent, "output");
        var result = Run(NewArguments(target, root: source));
        Assert(result.Code == 3, "Invalid source policy must be structured path/source exit3.");
        Assert(!(result.Output + result.Error).Contains("SUPER_SECRET_SOURCE_VALUE", StringComparison.Ordinal));
        using var error = JsonDocument.Parse(result.Error);
        Assert(error.RootElement.GetProperty("success").ValueKind == JsonValueKind.False);
        Assert(!Directory.Exists(target));
        SameSnapshot(before, parent);
        Assert(Directory.EnumerateDirectories(parent).Count() == 3, "Invalid source created staging/output.");
    });
}

foreach (var metacharacter in new[] { ";", "%", "$", "@" })
{
    Check("msbuild-metacharacter-in-source-path-rejected-" + metacharacter, () =>
    {
        var parent = Scope();
        var fixtureParent = Path.Combine(parent, "source" + metacharacter + "path");
        Directory.CreateDirectory(fixtureParent);
        var source = CreateSourceFixture(fixtureParent);
        var before = Snapshot(parent);
        var target = Path.Combine(parent, "output");
        var result = Run(NewArguments(target, root: source));
        Assert(result.Code == 3, "Source path with MSBuild syntax must be rejected before output.");
        SameSnapshot(before, parent);
        Assert(!Directory.Exists(target) && Directory.EnumerateFileSystemEntries(parent).Count() == 1);
    });
}

foreach (var pathNamespace in new[] { @"\\?\C:\NoIO", @"\\.\NUL" })
{
    foreach (var argument in new[] { "output", "engine-root", "config" })
    {
        Check("device-namespace-rejected-before-io-" + argument + "-" + pathNamespace, () =>
        {
            var parent = Scope();
            var target = Path.Combine(parent, "output");
            var arguments = argument switch
            {
                "output" => NewArguments(pathNamespace),
                "engine-root" => NewArguments(target, root: pathNamespace),
                _ => new[] { "validate", "--config", pathNamespace }
            };
            var result = Run(arguments);
            Assert(result.Code == 3);
            using var error = JsonDocument.Parse(result.Error);
            Assert(error.RootElement.GetProperty("code").GetString() == "path.namespace", "Device path must be rejected lexically before filesystem access.");
            AssertNoPublish(parent, target);
        });
    }
}

if (OperatingSystem.IsWindows())
{
    Check("windows-extended-output-path-cannot-bypass-checkout-boundary", () =>
    {
        var parent = Scope();
        var source = CreateSourceFixture(parent);
        var target = Path.Combine(source, "src", "UnexpectedOutput");
        var before = Snapshot(parent);
        Assert(Run(NewArguments(@"\\?\" + target, root: source)).Code == 3);
        Assert(!Directory.Exists(target));
        SameSnapshot(before, parent);
        Assert(!Directory.EnumerateDirectories(Path.Combine(source, "src")).Any(path => Path.GetFileName(path).StartsWith(".cehpalon-stage-", StringComparison.Ordinal)));
    });
    Check("windows-extended-source-path-is-rejected-before-output", () =>
    {
        var parent = Scope();
        var source = CreateSourceFixture(parent);
        var before = Snapshot(parent);
        var target = Path.Combine(parent, "output");
        Assert(Run(NewArguments(target, root: @"\\?\" + source)).Code == 3);
        Assert(!Directory.Exists(target));
        SameSnapshot(before, parent);
        Assert(Directory.EnumerateDirectories(parent).Count() == 3);
    });
    Check("windows-extended-config-path-is-rejected-without-modification", () =>
    {
        var parent = Scope();
        var target = Path.Combine(parent, "output");
        Assert(Run(NewArguments(target)).Code == 0);
        var before = Snapshot(parent);
        Assert(Run("validate", "--config", @"\\?\" + Path.Combine(target, "appsettings.json")).Code == 3);
        SameSnapshot(before, parent);
    });
}

Check("existing-directory-symlink-is-not-followed-or-removed", () =>
{
    var parent = Scope();
    var external = Path.Combine(parent, "external");
    var target = Path.Combine(parent, "output");
    Directory.CreateDirectory(external);
    File.WriteAllText(Path.Combine(external, "sentinel"), "outside-content");
    Directory.CreateSymbolicLink(target, external);
    var before = Snapshot(external);
    Assert(Run(NewArguments(target)).Code == 3);
    Assert(new DirectoryInfo(target).LinkTarget is not null, "Existing symlink was removed or replaced.");
    SameSnapshot(before, external);
    Assert(Directory.EnumerateFileSystemEntries(parent).Count() == 2);
});

Check("dangling-directory-symlink-is-not-replaced", () =>
{
    var parent = Scope();
    var missing = Path.Combine(parent, "not-created");
    var target = Path.Combine(parent, "output");
    Directory.CreateSymbolicLink(target, missing);
    Assert(Run(NewArguments(target)).Code == 3);
    Assert(new DirectoryInfo(target).LinkTarget is not null, "Dangling symlink was removed or replaced.");
    Assert(!Directory.Exists(missing));
    Assert(Directory.EnumerateFileSystemEntries(parent).Count() == 1);
});

Check("output-parent-symlink-is-not-followed", () =>
{
    var parent = Scope();
    var external = Path.Combine(parent, "external");
    var link = Path.Combine(parent, "linked-parent");
    Directory.CreateDirectory(external);
    File.WriteAllBytes(Path.Combine(external, "sentinel"), [0, 255, 13, 10]);
    Directory.CreateSymbolicLink(link, external);
    var before = Snapshot(external);
    Assert(Run(NewArguments(Path.Combine(link, "output"))).Code == 3);
    Assert(new DirectoryInfo(link).LinkTarget is not null);
    SameSnapshot(before, external);
    Assert(Directory.EnumerateFileSystemEntries(parent).Count() == 2);
});

Check("source-input-symlink-is-not-followed", () =>
{
    var parent = Scope();
    var source = CreateSourceFixture(parent);
    var realInput = Path.Combine(parent, "external-global.json");
    File.Copy(Path.Combine(source, "global.json"), realInput);
    File.Delete(Path.Combine(source, "global.json"));
    File.CreateSymbolicLink(Path.Combine(source, "global.json"), realInput);
    var before = File.ReadAllBytes(realInput);
    var target = Path.Combine(parent, "output");
    Assert(Run(NewArguments(target, root: source)).Code == 3);
    Assert(new FileInfo(Path.Combine(source, "global.json")).LinkTarget is not null);
    Assert(File.ReadAllBytes(realInput).SequenceEqual(before));
    Assert(!Directory.Exists(target) && Directory.EnumerateDirectories(parent).Count() == 3);
});

Check("valid-generated-config-validates-without-changing-files", () =>
{
    var parent = Scope();
    var target = Path.Combine(parent, "output");
    Assert(Run(NewArguments(target)).Code == 0);
    var before = Snapshot(parent);
    var result = Run("validate", "--config", Path.Combine(target, "appsettings.json"));
    Assert(result.Code == 0 && result.Error.Length == 0, "Generated config rejected: " + result.Error);
    SameSnapshot(before, parent);
});

foreach (var setting in new (string Name, string From, string To)[]
{
    ("unsupported-topology", "SingleProcess", "Distributed"),
    ("unsupported-pattern", "RequestReply", "EventSourcing"),
    ("unsupported-architecture", "Modular", "Microservices")
})
{
    Check("generated-config-rejects-" + setting.Name + "-without-mutation", () =>
    {
        var parent = Scope();
        var target = Path.Combine(parent, "output");
        Assert(Run(NewArguments(target)).Code == 0);
        var path = Path.Combine(target, "appsettings.json");
        var original = File.ReadAllText(path);
        Assert(original.Contains(setting.From, StringComparison.Ordinal), "Expected baseline config setting not present.");
        File.WriteAllText(path, original.Replace(setting.From, setting.To, StringComparison.Ordinal), new UTF8Encoding(false));
        var before = Snapshot(parent);
        var result = Run("validate", "--config", path);
        AssertDiagnostic(result, "config.invalid", setting.To, "CFG_PROFILE");
        SameSnapshot(before, parent);
    });
}

foreach (var profile in new[] { "portable-request-reply", "small-api" })
foreach (var setting in new (string Field, string Supported)[]
{
    ("Architecture", "Modular"),
    ("Topology", "SingleProcess"),
    ("Pattern", "RequestReply")
})
{
    Check($"{profile}-unsupported-{setting.Field}-suggests-supported-value", () =>
    {
        var parent = Scope();
        var target = Path.Combine(parent, "consumer");
        Assert(Run([.. NewArguments(target), "--profile", profile]).Code == 0);
        var path = Path.Combine(target, "appsettings.json");
        var config = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
        const string rejectedValue = "rejected-tuple-value-fixture";
        config["Cehpalon"]![setting.Field] = rejectedValue;
        File.WriteAllText(path, config.ToJsonString());
        var before = Snapshot(parent);
        var result = Run("validate", "--profile", profile, "--config", path);
        AssertDiagnostic(result, "config.invalid", rejectedValue, "CFG_PROFILE");
        using var error = JsonDocument.Parse(result.Error);
        var diagnostic = error.RootElement.GetProperty("diagnostics").EnumerateArray().Single();
        Assert(diagnostic.TryGetProperty("hint", out var hint)
            && hint.GetString() == $"$.Cehpalon.{setting.Field}: Only {setting.Supported} is supported in schema 1.",
            "Expected a hint naming the rejected setting and its supported value.");
        Assert(result.Error == $$"""{"success":false,"code":"config.invalid","message":"Configuration was rejected before activation.","diagnostics":[{"code":"CFG_PROFILE","path":"$.Cehpalon","message":"Configuration violates the selected profile.","hint":"$.Cehpalon.{{setting.Field}}: Only {{setting.Supported}} is supported in schema 1."}]}""" + Environment.NewLine,
            "Profile tuple rejection stderr changed.");
        SameSnapshot(before, parent);
    });
}

Check("mixed-config-diagnostics-hint-only-for-profile-and-redact-input", () =>
{
    var parent = Scope();
    var path = Path.Combine(parent, "config.json");
    const string rejectedVersion = "947631";
    const string rejectedArchitecture = "rejected-architecture-fixture";
    const string rejectedPattern = "rejected-pattern-fixture";
    const string rejectedModules = "rejected-modules-fixture";
    File.WriteAllText(path, $$"""
        {
          "Cehpalon": {
            "SchemaVersion": {{rejectedVersion}},
            "Architecture": "{{rejectedArchitecture}}",
            "Topology": "SingleProcess",
            "Pattern": "{{rejectedPattern}}",
            "Modules": "{{rejectedModules}}"
          }
        }
        """);
    var before = Snapshot(parent);
    var result = Run("validate", "--config", path);
    AssertDiagnostic(result, "config.invalid", nestedCode: "CFG_PROFILE");
    foreach (var rejected in new[] { rejectedVersion, rejectedArchitecture, rejectedPattern, rejectedModules })
        Assert(!result.Error.Contains(rejected, StringComparison.Ordinal), "Stderr echoed rejected configuration input.");

    using var error = JsonDocument.Parse(result.Error);
    var diagnostics = error.RootElement.GetProperty("diagnostics").EnumerateArray().ToArray();
    Assert(diagnostics.Select(diagnostic => diagnostic.GetProperty("code").GetString()).Order(StringComparer.Ordinal)
        .SequenceEqual(["CFG_MODULES", "CFG_PROFILE", "CFG_PROFILE", "CFG_VERSION"]), "Expected both profile and non-profile rejections.");
    var profileHints = new List<string?>();
    foreach (var diagnostic in diagnostics)
    {
        var code = diagnostic.GetProperty("code").GetString();
        var hasHint = diagnostic.TryGetProperty("hint", out var hint);
        Assert(hasHint == (code == "CFG_PROFILE"), $"Unexpected hint presence for {code}.");
        if (hasHint) profileHints.Add(hint.GetString());
    }
    Assert(profileHints.Order(StringComparer.Ordinal).SequenceEqual([
        "$.Cehpalon.Architecture: Only Modular is supported in schema 1.",
        "$.Cehpalon.Pattern: Only RequestReply is supported in schema 1."]), "Expected one fixed hint for each rejected tuple field.");
    SameSnapshot(before, parent);
});

Check("unknown-module-config-diagnostic-does-not-disclose-module-key", () =>
{
    var parent = Scope();
    var target = Path.Combine(parent, "output");
    Assert(Run(NewArguments(target)).Code == 0);
    var path = Path.Combine(target, "appsettings.json");
    var text = File.ReadAllText(path);
    Assert(text.Contains("\"app\"", StringComparison.Ordinal));
    File.WriteAllText(path, text.Replace("\"app\"", "\"SUPER_SECRET_CONFIG_VALUE\"", StringComparison.Ordinal), new UTF8Encoding(false));
    var before = Snapshot(parent);
    var result = Run("validate", "--config", path);
    Assert(result.Code == 2);
    Assert(!(result.Output + result.Error).Contains("SUPER_SECRET_CONFIG_VALUE", StringComparison.Ordinal), "Unknown module key leaked in diagnostic code/path/message.");
    using var error = JsonDocument.Parse(result.Error);
    var diagnostic = error.RootElement.GetProperty("diagnostics").EnumerateArray().Single();
    Assert(diagnostic.GetProperty("code").GetString() == "CFG_UNKNOWN_MODULE" && !diagnostic.TryGetProperty("hint", out _),
        "Only unsupported profile tuple diagnostics may include a hint.");
    Assert(result.Error == """{"success":false,"code":"config.invalid","message":"Configuration was rejected before activation.","diagnostics":[{"code":"CFG_UNKNOWN_MODULE","path":"$.Cehpalon","message":"Configuration violates the selected profile."}]}""" + Environment.NewLine,
        "Portable unknown-module rejection stderr changed.");
    SameSnapshot(before, parent);
});

Check("missing-config-has-path-exit-without-creation", () =>
{
    var parent = Scope();
    var path = Path.Combine(parent, "missing.json");
    Assert(Run("validate", "--config", path).Code == 3);
    Assert(!Directory.EnumerateFileSystemEntries(parent).Any());
});

Check("directory-config-is-rejected-without-mutation", () =>
{
    var parent = Scope();
    Assert(Run("validate", "--config", parent).Code == 3);
    Assert(!Directory.EnumerateFileSystemEntries(parent).Any());
});

foreach (var invalid in new (string Name, string Text)[]
{
    ("empty", ""),
    ("malformed", "{ not-json SUPER_SECRET_CONFIG_VALUE"),
    ("no-engine-section", "{\"ConnectionStrings\":{\"Secret\":\"SUPER_SECRET_CONFIG_VALUE\"}}"),
    ("scalar-root", "\"SUPER_SECRET_CONFIG_VALUE\""),
    ("array-root", "[\"SUPER_SECRET_CONFIG_VALUE\"]"),
    ("duplicate-engine-sections", "{\"Cehpalon\":{},\"Cehpalon\":{\"secret\":\"SUPER_SECRET_CONFIG_VALUE\"}}")
})
{
    Check("invalid-config-" + invalid.Name + "-preserves-bytes-and-redacts-content", () =>
    {
        var parent = Scope();
        var path = Path.Combine(parent, "config.json");
        File.WriteAllText(path, invalid.Text, new UTF8Encoding(false));
        var before = Snapshot(parent);
        var result = Run("validate", "--config", path);
        Assert(result.Code == 2, "Invalid config must be exit2, got " + result.Code);
        Assert(!(result.Output + result.Error).Contains("SUPER_SECRET_CONFIG_VALUE", StringComparison.Ordinal), "Config content leaked into CLI output.");
        SameSnapshot(before, parent);
    });
}

Check("oversize-config-rejected-without-content-disclosure", () =>
{
    var parent = Scope();
    var path = Path.Combine(parent, "large.json");
    File.WriteAllText(path, "{\"Secret\":\"SUPER_SECRET_CONFIG_VALUE" + new string('x', 1024 * 1024) + "\"}", new UTF8Encoding(false));
    var before = Snapshot(parent);
    var result = Run("validate", "--config", path);
    Assert(result.Code == 2);
    Assert(!(result.Output + result.Error).Contains("SUPER_SECRET_CONFIG_VALUE", StringComparison.Ordinal));
    SameSnapshot(before, parent);
});

Check("invalid-utf8-config-is-rejected-without-modification", () =>
{
    var parent = Scope();
    var path = Path.Combine(parent, "invalid-utf8.json");
    File.WriteAllBytes(path, [0x7B, 0x22, 0xC3, 0x28, 0x22, 0x3A, 0x30, 0x7D]);
    var before = Snapshot(parent);
    Assert(Run("validate", "--config", path).Code == 2);
    SameSnapshot(before, parent);
});

string[] SmallArguments(string target) => [.. NewArguments(target), "--profile", "small-api"];
string[] smallFiles = ["Catalog.cs", "Consumer.csproj", "ContractExport.cs", "FixtureHost.cs", "NuGet.Config", "Program.cs", "README.md", "SelfTest.cs",
    "appsettings.graphql-only.json", "appsettings.graphql.json", "appsettings.json", "appsettings.off.json", "engine-source.json", "global.json"];

Check("small-api-release-candidate-jwt-policy-rejected-before-output", () =>
{
    var parent = Scope(); var source = CreateSourceFixture(parent);
    var policy = Path.Combine(source, "Directory.Packages.props");
    var text = File.ReadAllText(policy);
    Assert(text.Contains("[11.0.0]", StringComparison.Ordinal), "Fixture must start at the stable JWT pin.");
    File.WriteAllText(policy, text.Replace("[11.0.0]", "[11.0.0-rc.3.26522.148]", StringComparison.Ordinal), new UTF8Encoding(false));
    var before = Snapshot(parent); var target = Path.Combine(parent, "output");
    var result = Run([.. NewArguments(target, root: source), "--profile", "small-api"]);
    Assert(result.Code == 3 && !Directory.Exists(target));
    SameSnapshot(before, parent);
});

Check("small-api-exact-files-determinism-and-profile-specific-validation", () =>
{
    var parent = Scope(); var first = Path.Combine(parent, "first"); var second = Path.Combine(parent, "second");
    Assert(Run(SmallArguments(first)).Code == 0); Assert(Run(SmallArguments(second)).Code == 0);
    Assert(Directory.EnumerateFiles(first).Select(Path.GetFileName).Order(StringComparer.Ordinal).SequenceEqual(smallFiles.Order(StringComparer.Ordinal)));
    SameSnapshot(Snapshot(first), second);
    using var sdkPolicy = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(first, "global.json")));
    Assert(sdkPolicy.RootElement.GetProperty("sdk").GetProperty("version").GetString() == "11.0.100");
    Assert(sdkPolicy.RootElement.GetProperty("sdk").GetProperty("allowPrerelease").ValueKind == JsonValueKind.False);
    var projectText = File.ReadAllText(Path.Combine(first, "Consumer.csproj"));
    Assert(projectText.Contains("Microsoft.AspNetCore.Authentication.JwtBearer\" Version=\"[11.0.0]\"", StringComparison.Ordinal));
    Assert(!projectText.Contains("-rc.", StringComparison.Ordinal), "A generated current project must not retain an RC package reference.");
    foreach (var name in new[] { "appsettings.json", "appsettings.graphql.json", "appsettings.graphql-only.json", "appsettings.off.json" })
    {
        var config = Path.Combine(first, name); var before = Snapshot(first);
        var result = Run("validate", "--profile", "small-api", "--config", config);
        Assert(result.Code == 0 && result.Error.Length == 0);
        using var json = JsonDocument.Parse(result.Output);
        Assert(json.RootElement.GetProperty("profile").GetString() == "small-api");
        SameSnapshot(before, first);
    }
    Assert(Run("validate", "--config", Path.Combine(first, "appsettings.json")).Code == 2,
        "Small API config must not be silently accepted by the portable catalog.");
});

foreach (var capability in new[] { "worker", "database", "cache", "broker", "ai", "retrieval" })
{
    Check("small-api-pure-validation-rejects-unsupported-" + capability, () =>
    {
        var parent = Scope(); var target = Path.Combine(parent, "consumer"); Assert(Run(SmallArguments(target)).Code == 0);
        var path = Path.Combine(target, "appsettings.json");
        var config = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
        config["Cehpalon"]!["Modules"]![capability] = new System.Text.Json.Nodes.JsonObject { ["Enabled"] = true };
        File.WriteAllText(path, config.ToJsonString()); var before = Snapshot(target);
        var result = Run("validate", "--profile", "small-api", "--config", path);
        AssertDiagnostic(result, "config.capability"); SameSnapshot(before, target);
    });
}

string CapabilityConfig(string path, string fixtureValue, params string[] enabledModules)
{
    var modules = string.Join(",", enabledModules.Select(id => "\"" + id + "\":{\"Enabled\":true}"));
    File.WriteAllText(path, $$"""
        {
          "Unrelated": "{{fixtureValue}}",
          "Cehpalon": {
            "SchemaVersion": 1, "Architecture": "Modular", "Topology": "SingleProcess", "Pattern": "RequestReply",
            "Modules": { {{modules}} }
          }
        }
        """);
    return path;
}

void AssertCapabilityDiagnostic(CliResult result, string[] unsupported, string? redacted = null)
{
    AssertDiagnostic(result, "config.capability", redacted);
    var expected = $$"""{"success":false,"code":"config.capability","message":"The small-api profile supports only host, API and GraphQL activation; disable unsupported capabilities.","unsupported":{{JsonSerializer.Serialize(unsupported)}},"supported":["api","graphql","host"]}""";
    Assert(result.Error == expected + Environment.NewLine, "Unexpected capability rejection stderr: " + result.Error);
}

foreach (var capability in new[] { "worker", "database", "cache", "broker", "ai", "retrieval" })
{
    Check("small-api-capability-diagnostic-lists-only-" + capability, () =>
    {
        var path = CapabilityConfig(Path.Combine(Scope(), "config.json"), "unrelated-fixture", capability);
        AssertCapabilityDiagnostic(Run("validate", "--profile", "small-api", "--config", path), [capability]);
    });
}

Check("small-api-capability-diagnostic-lists-sorted-unique-unsupported-only", () =>
{
    const string unrelated = "unrelated-config-value-fixture";
    var path = CapabilityConfig(Path.Combine(Scope(), "config.json"), unrelated, "worker", "graphql", "database", "api", "cache", "ai");
    AssertCapabilityDiagnostic(Run("validate", "--profile", "small-api", "--config", path), ["ai", "cache", "database", "worker"], unrelated);
});

Check("small-api-unknown-module-rejection-precedes-capability-diagnostic", () =>
{
    const string unknown = "unknown-module-fixture";
    var path = CapabilityConfig(Path.Combine(Scope(), "config.json"), "unrelated-fixture", "worker", unknown);
    var result = Run("validate", "--profile", "small-api", "--config", path);
    AssertDiagnostic(result, "config.invalid", unknown, "CFG_UNKNOWN_MODULE");
    Assert(result.Error == """{"success":false,"code":"config.invalid","message":"Configuration was rejected before activation.","diagnostics":[{"code":"CFG_UNKNOWN_MODULE","path":"$.Cehpalon","message":"Configuration violates the selected profile."}]}""" + Environment.NewLine,
        "Invalid configuration must keep its existing stderr shape.");
});

Check("small-api-unknown-option-rejection-keeps-exact-stderr", () =>
{
    var result = Run("validate", "--profile", "small-api", "--unknown", "rejected-option-fixture");
    AssertDiagnostic(result, "usage.option", "rejected-option-fixture");
    Assert(result.Error == """{"success":false,"code":"usage.option","message":"Unknown option; use --help for supported arguments."}""" + Environment.NewLine);
});

Check("small-api-missing-config-rejection-keeps-exact-stderr", () =>
{
    var path = Path.Combine(Scope(), "missing.json");
    var result = Run("validate", "--profile", "small-api", "--config", path);
    Assert(result.Code == 3 && result.Output.Length == 0);
    Assert(result.Error == """{"success":false,"code":"path.io","message":"The source, configuration or output path could not be safely read or written."}""" + Environment.NewLine);
});

for (var position = 1; position <= 14; position++)
foreach (var after in new[] { false, true })
{
    var fault = position; var afterWrite = after;
    Check($"small-api-write-failure-{fault}-{afterWrite}-preserves-neighbor", () =>
    {
        var parent = Scope(); var target = Path.Combine(parent, "consumer"); var neighbor = Path.Combine(parent, "neighbor.txt");
        File.WriteAllText(neighbor, "unrelated bytes"); var before = Snapshot(parent);
        var physical = new PhysicalToolingFileSystem(); var writes = 0;
        var fs = new DelegatingFileSystem((path, bytes) =>
        {
            writes++;
            if (writes == fault && !afterWrite) throw new IOException("SUPER_SECRET_FAILURE");
            physical.WriteAllBytes(path, bytes);
            if (writes == fault && afterWrite) throw new IOException("SUPER_SECRET_FAILURE");
        }, physical.MoveDirectory);
        var result = Invoke(SmallArguments(target), fs);
        Assert(result.Code == 3 && writes == fault && !result.Error.Contains("SUPER_SECRET_FAILURE", StringComparison.Ordinal));
        Assert(!Directory.Exists(target) && !Directory.EnumerateDirectories(parent).Any()); SameSnapshot(before, parent);
    });
}

Check("small-api-publication-collision-keeps-outsider-bytes", () =>
{
    var parent = Scope(); var target = Path.Combine(parent, "consumer"); var physical = new PhysicalToolingFileSystem();
    var fs = new DelegatingFileSystem(physical.WriteAllBytes, (stage, destination) =>
    {
        Directory.CreateDirectory(destination); File.WriteAllText(Path.Combine(destination, "outsider.txt"), "owned by another writer");
        physical.MoveDirectory(stage, destination);
    });
    Assert(Invoke(SmallArguments(target), fs).Code == 3);
    Assert(File.ReadAllText(Path.Combine(target, "outsider.txt")) == "owned by another writer");
    Assert(Directory.EnumerateDirectories(parent).Single() == target && Directory.EnumerateFiles(target).Count() == 1);
});

var output = new
{
    schemaVersion = 1,
    timestampUtc = DateTimeOffset.UtcNow,
    runtime = RuntimeInformation.FrameworkDescription,
    os = RuntimeInformation.OSDescription,
    architecture = RuntimeInformation.ProcessArchitecture.ToString(),
    scope = "Experimental portable CLI contracts: parsing, deterministic file generation, non-destructive path handling and config validation. Not generated-consumer compilation, real transports/providers, deployment qualification, full tooling acceptance or performance proof.",
    fixtureRoot = runRoot,
    passed = checks.Count - failures,
    failed = failures,
    tests = checks
};
var json = JsonSerializer.Serialize(output, new JsonSerializerOptions { WriteIndented = true });
var outputArgument = args.SingleOrDefault(value => value.StartsWith("--output=", StringComparison.Ordinal));
if (outputArgument is not null)
{
    var path = Path.GetFullPath(outputArgument[9..]);
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllText(path, json + "\n", new UTF8Encoding(false));
}
Console.WriteLine(json);
return failures == 0 ? 0 : 1;

static string FindEngineRoot()
{
    for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        if (File.Exists(Path.Combine(directory.FullName, "src", "Cehpalon.Engine.Core", "Cehpalon.Engine.Core.csproj")))
            return directory.FullName;
    throw new InvalidOperationException("Cannot locate Engine source root.");
}

sealed record CliResult(int Code, string Output, string Error);

sealed class DelegatingFileSystem(Action<string, byte[]> write, Action<string, string> move) : IToolingFileSystem
{
    public void WriteAllBytes(string path, byte[] bytes) => write(path, bytes);
    public void MoveDirectory(string source, string destination) => move(source, destination);
}
