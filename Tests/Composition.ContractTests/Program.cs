using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cehpalon.Engine.Composition;

var cases = new List<(string Name, Func<Task> Run)>();
var results = new List<object>();
void Add(string name, Action action) => cases.Add((name, () => { action(); return Task.CompletedTask; }));
void Async(string name, Func<Task> action) => cases.Add((name, action));
void Assert(bool value, string message = "Assertion failed") { if (!value) throw new Exception(message); }
T Throws<T>(Action action) where T : Exception
{
    try { action(); } catch (T error) { return error; }
    throw new Exception("Expected " + typeof(T).Name);
}
async Task<T> ThrowsAsync<T>(Func<Task> action) where T : Exception
{
    try { await action(); } catch (T error) { return error; }
    throw new Exception("Expected " + typeof(T).Name);
}
string Json(params (string Id, bool Enabled)[] selections) => JsonSerializer.Serialize(new
{
    Cehpalon = new { SchemaVersion = 1, Architecture = "Modular", Topology = "SingleProcess", Pattern = "RequestReply",
        Modules = selections.ToDictionary(x => x.Id, x => new { x.Enabled }) }
});
ModuleDescriptor[] Catalog() => [new("core", ModuleRole.Support), new("api", ModuleRole.EntryPoint, ["core"]),
    new("graphql", ModuleRole.EntryPoint, ["core"]), new("worker", ModuleRole.EntryPoint, ["core"])];
ActivationPlan Plan(string json, IEnumerable<ModuleDescriptor>? modules = null)
{
    var result = CompositionCompiler.Compile(json, modules ?? Catalog());
    Assert(result.IsSuccess, string.Join("; ", result.Diagnostics));
    return result.Plan!;
}
void Invalid(string json, string code, IEnumerable<ModuleDescriptor>? modules = null)
{
    var result = CompositionCompiler.Compile(json, modules ?? Catalog());
    Assert(!result.IsSuccess && result.Plan is null && result.Diagnostics.Any(d => d.Code == code), "Expected " + code + ": " + string.Join(";", result.Diagnostics));
}

const string publicationFaultProbe = "--composition-metrics-publication-fault";
const string publicationReentryProbe = "--composition-metrics-publication-reentry";
if (args is [publicationFaultProbe])
{
    await CheckPublicationFault();
    return 0;
}
if (args is [publicationReentryProbe])
{
    await CheckPublicationReentry();
    return 0;
}

bool IsLifecycleHistogram(Instrument instrument) =>
    instrument.Meter.Name == "Cehpalon.Engine.Composition" &&
    instrument.Name == "cehpalon.module.lifecycle.duration";

async Task RunPublicationProbe(string probe)
{
    var executable = Environment.ProcessPath ?? throw new InvalidOperationException("The test host path is unavailable.");
    var start = new ProcessStartInfo(executable)
    {
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true
    };
    if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
    start.ArgumentList.Add(probe);
    using var child = Process.Start(start) ?? throw new InvalidOperationException("The publication probe did not start.");
    var output = Task.WhenAll(child.StandardOutput.ReadToEndAsync(), child.StandardError.ReadToEndAsync());
    using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(15));
    try { await child.WaitForExitAsync(watchdog.Token); }
    catch (OperationCanceledException) when (watchdog.IsCancellationRequested)
    {
        if (!child.HasExited) child.Kill();
        await child.WaitForExitAsync();
        throw new TimeoutException("The publication probe did not exit: " + probe);
    }
    Assert(child.ExitCode == 0, string.Join(Environment.NewLine, await output));
}

async Task CheckPublicationFault()
{
    var publications = 0;
    var completions = 0;
    var calls = 0;
    var disabledCalls = 0;
    var events = new List<string>();
    var active = Plan(Json(("core", true)));
    var core = new ModuleRegistration("core", _ =>
    {
        calls++;
        return ValueTask.FromResult<IAsyncDisposable>(new Resource("core", new Counter(), events));
    });
    using (var listener = new MeterListener())
    {
        listener.InstrumentPublished = (instrument, current) =>
        {
            if (!IsLifecycleHistogram(instrument)) return;
            publications++;
            current.EnableMeasurementEvents(instrument);
            throw new InvalidOperationException("Publication callback failed.");
        };
        listener.MeasurementsCompleted = (instrument, _) =>
        {
            if (!IsLifecycleHistogram(instrument)) return;
            completions++;
            throw new InvalidOperationException("Completion callback failed.");
        };
        listener.Start();

        var disabled = new ModuleRegistration("api", _ =>
        {
            disabledCalls++;
            return ValueTask.FromResult<IAsyncDisposable>(new Resource());
        });
        await using (await PlanActivator.ActivateAsync(Plan(Json(("api", false))), [disabled])) { }
        await ThrowsAsync<ArgumentException>(async () => await PlanActivator.ActivateAsync(active, []));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await ThrowsAsync<OperationCanceledException>(async () =>
            await PlanActivator.ActivateAsync(active, [core], cancellation.Token));
        Assert(publications == 0 && calls == 0 && disabledCalls == 0);

        await using (await PlanActivator.ActivateAsync(active, [core])) { }
        Assert(publications == 1 && completions == 1 && calls == 1 && events.SequenceEqual(["start:core", "stop:core"]));
    }

    var latePublications = 0;
    using var lateListener = new MeterListener
    {
        InstrumentPublished = (instrument, _) => { if (IsLifecycleHistogram(instrument)) latePublications++; }
    };
    lateListener.Start();
    await using (await PlanActivator.ActivateAsync(active, [core])) { }
    var failure = new ApplicationException("Activation failure.");
    var rolledBack = await ThrowsAsync<ApplicationException>(async () =>
        await PlanActivator.ActivateAsync(Plan(Json(("api", true))), [core, new("api", _ => throw failure)]));
    Assert(ReferenceEquals(failure, rolledBack) && publications == 1 && latePublications == 0 &&
        calls == 3 && events.SequenceEqual(["start:core", "stop:core", "start:core", "stop:core", "start:core", "stop:core"]));
}

async Task CheckPublicationReentry()
{
    var publications = 0;
    var measurements = new List<string>();
    var events = new List<string>();
    var plan = Plan(Json(("core", true)));
    using var cancellation = new CancellationTokenSource();
    using var listener = new MeterListener
    {
        InstrumentPublished = (instrument, current) =>
        {
            if (!IsLifecycleHistogram(instrument)) return;
            publications++;
            current.EnableMeasurementEvents(instrument);

            // Join a separate caller to catch a lock held across publication.
            Task.Run(async () =>
            {
                var nested = new ModuleRegistration("core", _ =>
                    ValueTask.FromResult<IAsyncDisposable>(new Resource("nested", new Counter(), events)));
                await using var lease = await PlanActivator.ActivateAsync(plan, [nested]);
            }).GetAwaiter().GetResult();
            cancellation.Cancel();
        }
    };
    listener.SetMeasurementEventCallback<double>((_, _, tags, _) => measurements.Add((string)tags[0].Value!));
    listener.Start();
    var outer = new ModuleRegistration("core", _ =>
        ValueTask.FromResult<IAsyncDisposable>(new Resource("outer", new Counter(), events)));
    var canceled = await ThrowsAsync<OperationCanceledException>(async () =>
        await PlanActivator.ActivateAsync(plan, [outer], cancellation.Token));
    Assert(canceled.CancellationToken == cancellation.Token && measurements.Count == 0 &&
        events.SequenceEqual(["start:nested", "stop:nested"]));
    await using (await PlanActivator.ActivateAsync(plan, [outer])) { }
    Assert(publications == 1 && measurements.SequenceEqual(["activation", "disposal"]) &&
        events.SequenceEqual(["start:nested", "stop:nested", "start:outer", "stop:outer"]));
}

Async("first publication and completion callback failures leave lifecycle intact", () => RunPublicationProbe(publicationFaultProbe));
Async("activation proceeds without metrics when a publication callback reenters", () => RunPublicationProbe(publicationReentryProbe));

Add("empty selections activate nothing", () => Assert(Plan(Json()).ActivationOrder.Count == 0));
Add("API-only plan explains implicit support and disabled entrypoints", () =>
{
    var plan = Plan(Json(("api", true), ("graphql", false), ("worker", false)));
    Assert(plan.ActivationOrder.SequenceEqual(["core", "api"]));
    Assert(plan.Modules.Single(x => x.Id == "core").Reason == "support dependency of api");
    Assert(plan.Explain().Contains("graphql: disabled (explicit Enabled:false)"));
});
Add("API plus GraphQL plan selects both explicit roots", () => Assert(Plan(Json(("api", true), ("graphql", true))).ActivationOrder.SequenceEqual(["core", "api", "graphql"])));
Add("worker-only plan does not select API", () => Assert(Plan(Json(("worker", true))).ActivationOrder.SequenceEqual(["core", "worker"])));
Add("explicit support root is allowed without entrypoints", () => Assert(Plan(Json(("core", true))).ActivationOrder.SequenceEqual(["core"])));
Add("explicit false dependency rejects plan", () => Invalid(Json(("api", true), ("core", false)), "PLAN_FORBIDDEN"));
Add("entrypoint dependency requires explicit root", () => Invalid(Json(("api", true)), "PLAN_ENTRYPOINT", [new("api", ModuleRole.EntryPoint, ["worker"]), new("worker", ModuleRole.EntryPoint)]));
Add("explicitly selected entrypoint dependency is allowed", () => Assert(Plan(Json(("api", true), ("worker", true)), [new("api", ModuleRole.EntryPoint, ["worker"]), new("worker", ModuleRole.EntryPoint)]).ActivationOrder.SequenceEqual(["worker", "api"])));
Add("topological tie order is ordinal independently of catalog input", () =>
{
    var a = Plan(Json(("api", true), ("worker", true), ("graphql", true)), Catalog());
    var b = Plan(Json(("worker", true), ("graphql", true), ("api", true)), Catalog().Reverse());
    Assert(a.ActivationOrder.SequenceEqual(b.ActivationOrder));
});
Add("unknown false module is rejected", () => Invalid(Json(("typo", false)), "CFG_UNKNOWN_MODULE"));
Add("duplicate catalog IDs rejected", () => Invalid(Json(), "CAT_DUPLICATE", [new("core", ModuleRole.Support), new("core", ModuleRole.Support)]));
Add("uppercase catalog IDs rejected", () => Invalid(Json(), "CAT_ID", [new("API", ModuleRole.EntryPoint)]));
Add("invalid catalog characters rejected", () => Invalid(Json(), "CAT_ID", [new("api/unsafe", ModuleRole.EntryPoint)]));
Add("catalog ID length limit enforced", () => Invalid(Json(), "CAT_ID", [new(new string('a', 129), ModuleRole.Support)]));
Add("unknown enum role rejected", () => Invalid(Json(), "CAT_ROLE", [new("core", (ModuleRole)99)]));
Add("null descriptor rejected", () => Invalid(Json(), "CAT_NULL", new ModuleDescriptor[] { null! }));
Add("missing dependency rejected in inactive catalog", () => Invalid(Json(), "CAT_MISSING", [new("api", ModuleRole.EntryPoint, ["absent"])]));
Add("duplicate dependency rejected", () => Invalid(Json(), "CAT_DEPENDENCY_DUPLICATE", [new("core", ModuleRole.Support), new("api", ModuleRole.EntryPoint, ["core", "core"])]));
Add("invalid dependency ID rejected", () => Invalid(Json(), "CAT_DEPENDENCY_ID", [new("api", ModuleRole.EntryPoint, ["Core"])]));
Add("inactive cycle rejected", () => Invalid(Json(), "CAT_CYCLE", [new("a", ModuleRole.Support, ["b"]), new("b", ModuleRole.Support, ["a"])]));
Add("self cycle rejected", () => Invalid(Json(), "CAT_CYCLE", [new("a", ModuleRole.Support, ["a"])]));
Add("catalog enumeration bounded", () => Invalid(Json(), "CAT_SIZE", Enumerable.Range(0, 257).Select(i => new ModuleDescriptor("m" + i, ModuleRole.Support))));
Add("dependency enumeration bounded", () => Invalid(Json(), "CAT_DEPENDENCIES", [new("a", ModuleRole.Support, Enumerable.Repeat("a", 1000))]));
Add("malformed JSON rejected without echoing input", () =>
{
    var result = CompositionCompiler.Compile("{SECRET_PROVIDER_PASSWORD", Catalog());
    Assert(!result.IsSuccess && result.Diagnostics.All(d => !d.ToString().Contains("SECRET_PROVIDER_PASSWORD")));
});
Add("malformed UTF16 cannot bypass JSON validation", () => Invalid("{\"Cehpalon\":\"\uD800\"}", "CFG_JSON"));
Add("escaped lone high surrogate in profile value yields safe diagnostic", () => Invalid(Json().Replace("Modular", "\\uD800"), "CFG_JSON"));
Add("escaped lone low surrogate in profile value yields safe diagnostic", () => Invalid(Json().Replace("Modular", "\\uDC00"), "CFG_JSON"));
Add("escaped lone surrogate module name yields safe diagnostic", () => Invalid(Json(("api", true)).Replace("\"api\"", "\"\\uD800\""), "CFG_JSON"));
Add("escaped lone surrogate property name yields safe diagnostic", () => Invalid(Json().Replace("\"Architecture\"", "\"\\uD800\""), "CFG_JSON"));
Add("valid surrogate pair in host section is preserved as valid JSON", () => Assert(Plan(Json().Replace("{\"Cehpalon", "{\"Description\":\"\\uD83D\\uDE00\",\"Cehpalon")).ActivationOrder.Count == 0));
Add("JSON root must be object", () => Invalid("[]", "CFG_ROOT"));
Add("engine section is required", () => Invalid("{}", "CFG_SECTION"));
Add("engine section casing is exact", () => Invalid(Json().Replace("Cehpalon", "cehpalon"), "CFG_SECTION"));
Add("engine section must be object", () => Invalid("{\"Cehpalon\":null}", "CFG_OBJECT"));
Add("top-level case ambiguity rejected", () => Invalid("{\"Cehpalon\":{},\"cehpalon\":{}}", "CFG_DUPLICATE"));
Add("normal host top-level sections coexist", () => Assert(Plan(Json().Replace("{\"Cehpalon", "{\"Logging\":{\"Default\":\"Information\"},\"Cehpalon")).ActivationOrder.Count == 0));
Add("unsupported schema version rejected", () => Invalid(Json().Replace("\"SchemaVersion\":1", "\"SchemaVersion\":2"), "CFG_VERSION"));
Add("string schema version rejected", () => Invalid(Json().Replace("\"SchemaVersion\":1", "\"SchemaVersion\":\"1\""), "CFG_VERSION"));
Add("missing schema version rejected", () => Invalid(Json().Replace("\"SchemaVersion\":1,", ""), "CFG_VERSION"));
Add("fractional schema version rejected", () => Invalid(Json().Replace("\"SchemaVersion\":1", "\"SchemaVersion\":1.5"), "CFG_VERSION"));
Add("unsupported topology rejected", () => Invalid(Json().Replace("SingleProcess", "Distributed"), "CFG_PROFILE"));
Add("unsupported pattern rejected", () => Invalid(Json().Replace("RequestReply", "EventDriven"), "CFG_PROFILE"));
Add("unsupported architecture rejected", () => Invalid(Json().Replace("Modular", "Microservices"), "CFG_PROFILE"));

// The CLI exposes these diagnostics as hints, so both fields must stay independent of input.
const string rejectedProfileValue = "rejected-profile-value-fixture";
foreach (var setting in new (string Field, string Supported)[]
{
    ("Architecture", "Modular"),
    ("Topology", "SingleProcess"),
    ("Pattern", "RequestReply")
})
foreach (var scenario in new (string Name, string? ValueJson)[]
{
    ("missing", null),
    ("non-string", $$"""{"fixture":"{{rejectedProfileValue}}"}"""),
    ("rejected-string", JsonSerializer.Serialize(rejectedProfileValue))
})
{
    Add($"CFG_PROFILE {setting.Field} {scenario.Name} has fixed path and message", () =>
    {
        var config = JsonNode.Parse(Json())!;
        var section = config["Cehpalon"]!.AsObject();
        if (scenario.ValueJson is null)
            Assert(section.Remove(setting.Field), "Expected baseline profile setting not present.");
        else
            section[setting.Field] = JsonNode.Parse(scenario.ValueJson);

        var result = CompositionCompiler.Compile(config.ToJsonString(), Catalog());
        Assert(!result.IsSuccess && result.Plan is null && result.Diagnostics.Count == 1, "Expected exactly one rejection.");
        var diagnostic = result.Diagnostics[0];
        Assert(diagnostic.Code == "CFG_PROFILE", "Expected CFG_PROFILE.");
        Assert(!diagnostic.Path.Contains(rejectedProfileValue, StringComparison.Ordinal)
            && !diagnostic.Message.Contains(rejectedProfileValue, StringComparison.Ordinal), "Profile diagnostic echoed rejected input.");
        Assert(diagnostic.Path == $"$.Cehpalon.{setting.Field}", "Unexpected profile diagnostic path.");
        Assert(diagnostic.Message == $"Only {setting.Supported} is supported in schema 1.", "Unexpected profile diagnostic message.");
    });
}

Add("unknown engine setting rejected", () => Invalid(Json().Replace("\"SchemaVersion\":1", "\"Unknown\":true,\"SchemaVersion\":1"), "CFG_UNKNOWN"));
Add("case-ambiguous engine setting rejected", () => Invalid(Json().Replace("\"SchemaVersion\":1", "\"schemaversion\":1,\"SchemaVersion\":1"), "CFG_DUPLICATE"));
Add("module selections must be object", () => Invalid(Json().Replace("\"Modules\":{}", "\"Modules\":[]"), "CFG_MODULES"));
Add("module selections casing is exact", () => Invalid(Json(("api", true)).Replace("\"api\"", "\"Api\""), "CFG_UNKNOWN_MODULE"));
Add("duplicate module selection rejected", () => Invalid(Json(("api", true)).Replace("\"api\":{\"Enabled\":true}", "\"api\":{\"Enabled\":true},\"api\":{\"Enabled\":false}"), "CFG_DUPLICATE"));
Add("case-ambiguous module selection rejected", () => Invalid(Json(("api", true)).Replace("\"api\":{\"Enabled\":true}", "\"api\":{\"Enabled\":true},\"API\":{\"Enabled\":false}"), "CFG_DUPLICATE"));
Add("Enabled string is rejected", () => Invalid(Json(("api", true)).Replace("\"Enabled\":true", "\"Enabled\":\"true\""), "CFG_ENABLED"));
Add("Enabled null is rejected", () => Invalid(Json(("api", true)).Replace("\"Enabled\":true", "\"Enabled\":null"), "CFG_ENABLED"));
Add("Enabled missing is rejected", () => Invalid(Json(("api", true)).Replace("\"Enabled\":true", ""), "CFG_ENABLED"));
Add("module settings must be object", () => Invalid(Json(("api", true)).Replace("{\"Enabled\":true}", "true"), "CFG_OBJECT"));
Add("unknown module setting rejected", () => Invalid(Json(("api", true)).Replace("\"Enabled\":true", "\"Unknown\":1,\"Enabled\":true"), "CFG_UNKNOWN"));
Add("Enabled case ambiguity rejected", () => Invalid(Json(("api", true)).Replace("\"Enabled\":true", "\"enabled\":true,\"Enabled\":true"), "CFG_DUPLICATE"));
Add("configuration byte limit enforced", () => Invalid(new string(' ', CompositionCompiler.MaximumConfigurationBytes + 1), "CFG_SIZE"));
Add("UTF8 byte limit enforced", () => Invalid(new string('ก', CompositionCompiler.MaximumConfigurationBytes / 2), "CFG_SIZE"));
Add("JSON depth limit enforced", () => Invalid(new string('[', 40) + "0" + new string(']', 40), "CFG_JSON"));
Add("module selection count bounded", () =>
{
    var selections = Enumerable.Range(0, 257).ToDictionary(i => "m" + i, _ => new { Enabled = false });
    var json = JsonSerializer.Serialize(new { Cehpalon = new { SchemaVersion = 1, Architecture = "Modular", Topology = "SingleProcess", Pattern = "RequestReply", Modules = selections } });
    Invalid(json, "CFG_MODULE_LIMIT");
});
Add("descriptor dependency input is snapshotted", () =>
{
    var dependencies = new[] { "core" }; var module = new ModuleDescriptor("api", ModuleRole.EntryPoint, dependencies);
    dependencies[0] = "missing";
    Assert(Plan(Json(("api", true)), [new("core", ModuleRole.Support), module]).ActivationOrder.SequenceEqual(["core", "api"]));
    Throws<NotSupportedException>(() => ((IList<string>)module.Dependencies)[0] = "changed");
});
Add("plan output collections cannot be mutated", () =>
{
    var plan = Plan(Json(("api", true)));
    Throws<NotSupportedException>(() => ((IList<string>)plan.ActivationOrder).Add("graphql"));
    Throws<NotSupportedException>(() => ((IList<ModuleDecision>)plan.Modules).Clear());
    Assert(typeof(ActivationPlan).GetConstructors().Length == 0);
});
Add("diagnostic output cannot be mutated", () =>
{
    var result = CompositionCompiler.Compile("[]", Catalog());
    Throws<NotSupportedException>(() => ((IList<CompositionDiagnostic>)result.Diagnostics).Clear());
});
Add("library dependency graph contains no host or provider assemblies", () =>
{
    var refs = typeof(CompositionCompiler).Assembly.GetReferencedAssemblies().Select(a => a.Name!).ToArray();
    Assert(refs.All(x => !x.StartsWith("Microsoft.AspNetCore") && !x.StartsWith("Microsoft.Extensions") && !x.StartsWith("Npgsql") && !x.StartsWith("RabbitMQ") && !x.StartsWith("Cehpalon.Engine.Core")));
});

foreach (var selections in new[] { new[] { ("api", true), ("graphql", false), ("worker", false) }, new[] { ("api", true), ("graphql", true), ("worker", false) }, new[] { ("api", false), ("graphql", false), ("worker", true) } })
{
    var name = string.Join("+", selections.Where(x => x.Item2).Select(x => x.Item1));
    Async(name + " recorder lifecycle gives disabled modules zero owned work", async () =>
    {
        var plan = Plan(Json(selections)); var counters = new Dictionary<string, Counter>(); var lifecycle = new List<string>();
        var registrations = Catalog().Select(m =>
        {
            var counter = counters[m.Id] = new Counter();
            return new ModuleRegistration(m.Id, _ =>
            {
                counter.Factory++;
                return ValueTask.FromResult<IAsyncDisposable>(new Resource(m.Id, counter, lifecycle));
            });
        }).ToArray();
        var lease = await PlanActivator.ActivateAsync(plan, registrations);
        foreach (var decision in plan.Modules)
        {
            var counter = counters[decision.Id];
            Assert(new[] { counter.Factory, counter.Constructor, counter.Connection, counter.Endpoint, counter.Background }.All(n => n == (decision.IsEnabled ? 1 : 0)), decision.Id);
        }
        Assert(lifecycle.SequenceEqual(plan.ActivationOrder.Select(id => "start:" + id)));
        await lease.DisposeAsync();
        Assert(lifecycle.Skip(plan.ActivationOrder.Count).SequenceEqual(plan.ActivationOrder.Reverse().Select(id => "stop:" + id)));
    });
}
Async("missing enabled registration preflight runs no factory", async () =>
{
    var calls = 0; var plan = Plan(Json(("api", true)));
    await ThrowsAsync<ArgumentException>(async () => await PlanActivator.ActivateAsync(plan, [new("core", _ => { calls++; return ValueTask.FromResult<IAsyncDisposable>(new Resource()); })]));
    Assert(calls == 0);
});
Async("duplicate registration preflight runs no factory", async () =>
{
    var calls = 0; var plan = Plan(Json(("core", true)));
    var registration = new ModuleRegistration("core", _ => { calls++; return ValueTask.FromResult<IAsyncDisposable>(new Resource()); });
    await ThrowsAsync<ArgumentException>(async () => await PlanActivator.ActivateAsync(plan, [registration, registration])); Assert(calls == 0);
});
Async("unknown registration preflight runs no factory", async () =>
{
    var calls = 0; var plan = Plan(Json(("core", true)));
    var registration = new ModuleRegistration("core", _ => { calls++; return ValueTask.FromResult<IAsyncDisposable>(new Resource()); });
    await ThrowsAsync<ArgumentException>(async () => await PlanActivator.ActivateAsync(plan, [registration, new("unknown", _ => throw new Exception())])); Assert(calls == 0);
});
Async("oversized registration enumeration bounded before factories", async () =>
{
    var calls = 0;
    var registrations = Enumerable.Repeat(new ModuleRegistration("core", _ => { calls++; return ValueTask.FromResult<IAsyncDisposable>(new Resource()); }), 1000);
    await ThrowsAsync<ArgumentException>(async () => await PlanActivator.ActivateAsync(Plan(Json(("core", true))), registrations)); Assert(calls == 0);
});
Async("pre-cancelled activation runs no factory", async () =>
{
    var calls = 0; using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
    await ThrowsAsync<OperationCanceledException>(async () => await PlanActivator.ActivateAsync(Plan(Json(("core", true))), [new("core", _ => { calls++; return ValueTask.FromResult<IAsyncDisposable>(new Resource()); })], cancellation.Token)); Assert(calls == 0);
});
Async("cancellation after acquired resource rolls it back before next factory", async () =>
{
    var events = new List<string>(); var api = 0; using var cancellation = new CancellationTokenSource();
    await ThrowsAsync<OperationCanceledException>(async () => await PlanActivator.ActivateAsync(Plan(Json(("api", true))), [new("core", token => { Assert(token == cancellation.Token); cancellation.Cancel(); return ValueTask.FromResult<IAsyncDisposable>(new Resource("core", new(), events)); }), new("api", _ => { api++; return ValueTask.FromResult<IAsyncDisposable>(new Resource()); })], cancellation.Token));
    Assert(api == 0 && events.SequenceEqual(["start:core", "stop:core"]));
});
Async("factory exception rolls back earlier modules and preserves original exception", async () =>
{
    var events = new List<string>(); var failure = new InvalidOperationException("factory failure");
    var observed = await ThrowsAsync<InvalidOperationException>(async () => await PlanActivator.ActivateAsync(Plan(Json(("api", true))), [new("core", _ => ValueTask.FromResult<IAsyncDisposable>(new Resource("core", new(), events))), new("api", _ => throw failure)]));
    Assert(ReferenceEquals(failure, observed) && events.SequenceEqual(["start:core", "stop:core"]));
});
Async("factory returning null triggers rollback", async () =>
{
    var events = new List<string>();
    await ThrowsAsync<InvalidOperationException>(async () => await PlanActivator.ActivateAsync(Plan(Json(("api", true))), [new("core", _ => ValueTask.FromResult<IAsyncDisposable>(new Resource("core", new(), events))), new("api", _ => ValueTask.FromResult<IAsyncDisposable>(null!))]));
    Assert(events.SequenceEqual(["start:core", "stop:core"]));
});
Async("aliased factory resource is rejected and disposed once", async () =>
{
    var events = new List<string>(); var resource = new Resource("shared", new(), events);
    await ThrowsAsync<InvalidOperationException>(async () => await PlanActivator.ActivateAsync(Plan(Json(("api", true))), [new("core", _ => ValueTask.FromResult<IAsyncDisposable>(resource)), new("api", _ => ValueTask.FromResult<IAsyncDisposable>(resource))]));
    Assert(events.SequenceEqual(["start:shared", "stop:shared"]));
});
Async("rollback cleanup errors preserve activation error first and attempt all cleanup", async () =>
{
    var events = new List<string>(); var failure = new InvalidOperationException("activation");
    var catalog = new[] { new ModuleDescriptor("a", ModuleRole.Support), new ModuleDescriptor("b", ModuleRole.Support, ["a"]), new ModuleDescriptor("c", ModuleRole.EntryPoint, ["b"]) };
    var error = await ThrowsAsync<AggregateException>(async () => await PlanActivator.ActivateAsync(Plan(Json(("c", true)), catalog), [new("a", _ => ValueTask.FromResult<IAsyncDisposable>(new Resource("a", new(), events, true))), new("b", _ => ValueTask.FromResult<IAsyncDisposable>(new Resource("b", new(), events, true))), new("c", _ => throw failure)]));
    Assert(ReferenceEquals(error.InnerExceptions[0], failure) && error.InnerExceptions.Count == 3 && events.SequenceEqual(["start:a", "start:b", "stop:b", "stop:a"]));
});
Async("lease cleanup attempts all resources and stays idempotent after errors", async () =>
{
    var events = new List<string>();
    var lease = await PlanActivator.ActivateAsync(Plan(Json(("api", true))), [new("core", _ => ValueTask.FromResult<IAsyncDisposable>(new Resource("core", new(), events, true))), new("api", _ => ValueTask.FromResult<IAsyncDisposable>(new Resource("api", new(), events, true)))]);
    var one = await ThrowsAsync<AggregateException>(async () => await lease.DisposeAsync());
    var two = await ThrowsAsync<AggregateException>(async () => await lease.DisposeAsync());
    Assert(ReferenceEquals(one, two) && one.InnerExceptions.Count == 2 && events.Count(x => x.StartsWith("stop:")) == 2);
});
Async("concurrent lease cleanup waits once for asynchronous disposal", async () =>
{
    var resource = new SlowResource();
    var lease = await PlanActivator.ActivateAsync(Plan(Json(("core", true))), [new("core", _ => ValueTask.FromResult<IAsyncDisposable>(resource))]);
    var first = lease.DisposeAsync().AsTask(); var second = lease.DisposeAsync().AsTask();
    Assert(!first.IsCompleted && !second.IsCompleted && resource.Calls == 1);
    resource.Complete.SetResult(); await Task.WhenAll(first, second); await lease.DisposeAsync(); Assert(resource.Calls == 1);
});
Async("empty plan permits zero registrations and disposable lease", async () =>
{
    var lease = await PlanActivator.ActivateAsync(Plan(Json()), []); await lease.DisposeAsync(); await lease.DisposeAsync();
});
Add("compilation is pure metadata even for invalid and valid plans", () =>
{
    // The compiler accepts no factory/delegate/host object anywhere in its public API.
    var method = typeof(CompositionCompiler).GetMethod("Compile")!;
    Assert(method.GetParameters().Select(p => p.ParameterType).SequenceEqual([typeof(string), typeof(IEnumerable<ModuleDescriptor>)]));
    Assert(Plan(Json(("api", true))).SchemaVersion == 1);
});

Async("module lifecycle measures attempted work and preserves cleanup errors", async () =>
{
    var measurements = new List<(string Phase, string Outcome)>();
    var validMeasurements = true;
    using var listener = new MeterListener();
    listener.InstrumentPublished = (instrument, current) =>
    {
        if (IsLifecycleHistogram(instrument))
        {
            validMeasurements &= instrument.Unit == "s";
            current.EnableMeasurementEvents(instrument);
        }
    };
    listener.SetMeasurementEventCallback<double>((_, seconds, tags, _) =>
    {
        validMeasurements &= double.IsFinite(seconds) && seconds >= 0 &&
            tags.Length == 2 && tags[0].Key == "phase" && tags[1].Key == "outcome";
        if (tags.Length == 2 && tags[0].Value is string phase && tags[1].Value is string outcome)
            measurements.Add((phase, outcome));
        throw new InvalidOperationException("Listener failure");
    });
    listener.Start();

    var disabledCalls = 0;
    var disabled = new ModuleRegistration("graphql", _ =>
    {
        disabledCalls++;
        return ValueTask.FromResult<IAsyncDisposable>(new Resource());
    });
    var plan = Plan(Json(("api", true), ("graphql", false)));
    var core = new ModuleRegistration("core", _ => ValueTask.FromResult<IAsyncDisposable>(new Resource()));
    var api = new ModuleRegistration("api", _ => ValueTask.FromResult<IAsyncDisposable>(new Resource()));
    var lease = await PlanActivator.ActivateAsync(plan, [core, api, disabled]);
    await lease.DisposeAsync();
    await lease.DisposeAsync();

    using var cancellation = new CancellationTokenSource();
    var failingCore = new ModuleRegistration("core", _ => ValueTask.FromResult<IAsyncDisposable>(
        new Resource("private", new Counter(), [], fail: true)));
    var canceledApi = new ModuleRegistration("api", token =>
    {
        cancellation.Cancel();
        return ValueTask.FromCanceled<IAsyncDisposable>(token);
    });
    var error = await ThrowsAsync<AggregateException>(async () =>
        await PlanActivator.ActivateAsync(plan, [failingCore, canceledApi, disabled], cancellation.Token));
    Assert(error.InnerExceptions.Count == 2 &&
        error.InnerExceptions[0] is OperationCanceledException canceled && canceled.CancellationToken == cancellation.Token &&
        error.InnerExceptions[1] is InvalidOperationException { Message: "cleanup:private" });

    Assert(disabledCalls == 0 && validMeasurements && measurements.SequenceEqual([
        ("activation", "success"), ("activation", "success"), ("disposal", "success"), ("disposal", "success"),
        ("activation", "success"), ("activation", "canceled"), ("rollback", "fault")]),
        $"Unexpected lifecycle measurements: {string.Join(", ", measurements)}.");
});

var failed = 0;
foreach (var test in cases)
{
    try { await test.Run(); results.Add(new { name = test.Name, passed = true }); }
    catch (Exception error) { failed++; results.Add(new { name = test.Name, passed = false, error = error.ToString() }); }
}
var report = new { observed_at_utc = DateTimeOffset.UtcNow, framework = RuntimeInformation.FrameworkDescription, os = RuntimeInformation.OSDescription,
    scope = "BCL Composition compiler/activation component fixtures only. Recorder API/GraphQL/Worker labels are not real hosts, transport/provider/tenant/E2E/benchmark/production proof. Windows is observational; #3 remains Blocked.",
    passed = cases.Count - failed, failed, total = cases.Count, results };
var output = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
var destination = args.FirstOrDefault(x => x.StartsWith("--output="))?[9..];
if (destination is not null) { Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destination))!); await File.WriteAllTextAsync(destination, output + Environment.NewLine); }
Console.WriteLine(output); return failed == 0 ? 0 : 1;

sealed class Counter { public int Factory, Constructor, Connection, Endpoint, Background; }
sealed class Resource : IAsyncDisposable
{
    private readonly string? id; private readonly List<string>? events; private readonly bool fail;
    public Resource() { }
    public Resource(string id, Counter counter, List<string> events, bool fail = false)
    {
        this.id = id; this.events = events; this.fail = fail;
        counter.Constructor++; counter.Connection++; counter.Endpoint++; counter.Background++; events.Add("start:" + id);
    }
    public ValueTask DisposeAsync() { events?.Add("stop:" + id); if (fail) throw new InvalidOperationException("cleanup:" + id); return ValueTask.CompletedTask; }
}
sealed class SlowResource : IAsyncDisposable
{
    public int Calls; public TaskCompletionSource Complete = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public async ValueTask DisposeAsync() { Interlocked.Increment(ref Calls); await Complete.Task; }
}
