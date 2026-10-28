using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Cehpalon.Engine.Core;
using Cehpalon.Engine.Transports.Http;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

var checks = new List<object>();
var failures = 0;
async Task Check(string name, Func<Task> action)
{
    try { await action(); checks.Add(new { name, passed = true }); }
    catch (Exception error) { failures++; checks.Add(new { name, passed = false, detail = error.ToString() }); }
}
void Assert(bool condition, string message = "Assertion failed")
{
    if (!condition) throw new InvalidOperationException(message);
}
var binding = new OperationBinding<Input, Output>("catalog.get", input => input.Id is > 0 and <= 10000 && !string.IsNullOrWhiteSpace(input.Tenant)
    ? null : new OperationError("validation.custom", "unsafe request-specific details"));
DefaultHttpContext Context(string json = "{\"tenant\":\"A\",\"id\":1}", bool authenticated = true, Probe? probe = null)
{
    var context = new DefaultHttpContext();
    context.Request.Method = "POST";
    context.Request.ContentType = "application/json";
    context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(json));
    context.Response.Body = new MemoryStream();
    context.User = new ClaimsPrincipal(new ClaimsIdentity([], authenticated ? "fixture" : null));
    context.TraceIdentifier = "server-correlation";
    if (probe is not null)
        context.RequestServices = new ServiceCollection().AddSingleton<ITransportOperationDispatcher>(probe).BuildServiceProvider();
    return context;
}
string Body(DefaultHttpContext context) => Encoding.UTF8.GetString(((MemoryStream)context.Response.Body).ToArray());

await Check("binding-validates-id-and-null-validator-without-work", () =>
{
    try { _ = new OperationBinding<Input, Output>("BAD", _ => null); throw new Exception("accepted ID"); }
    catch (ArgumentException) { }
    try { _ = new OperationBinding<Input, Output>("valid", null!); throw new Exception("accepted null"); }
    catch (ArgumentNullException) { }
    Assert(binding.OperationId == "catalog.get");
    return Task.CompletedTask;
});
await Check("anonymous-denies-before-body-validation-and-service-resolution", async () =>
{
    var context = Context("not-json", false);
    await OperationHttpAdapter.ExecuteAsync(context, binding);
    Assert(context.Response.StatusCode == 401 && context.Request.Body.Position == 0);
    Assert(Body(context).Contains("authentication.required"));
});
await Check("unvalidated-tenant-and-role-headers-do-not-authenticate", async () =>
{
    var probe = new Probe();
    var context = Context(authenticated: false, probe: probe);
    context.Request.Headers["X-Tenant"] = "A";
    context.Request.Headers["X-Role"] = "admin";
    context.Request.Headers.Authorization = "Bearer forged";
    await OperationHttpAdapter.ExecuteAsync(context, binding);
    Assert(context.Response.StatusCode == 401 && probe.Calls == 0);
});
await Check("valid-request-shares-binding-and-server-correlation", async () =>
{
    var probe = new Probe();
    var context = Context(probe: probe);
    context.Request.Headers["X-Correlation-ID"] = "caller-authority";
    context.Request.Headers["X-Tenant"] = "B";
    await OperationHttpAdapter.ExecuteAsync(context, binding);
    Assert(context.Response.StatusCode == 200 && probe.Calls == 1 && probe.LastInput == new Input("A", 1));
    Assert(probe.LastOperation == "catalog.get" && probe.LastContext?.CorrelationId == "server-correlation");
    Assert(probe.LastContext!.Metadata.Count == 0 && Body(context) == "{\"label\":\"allowed\"}");
});
await Check("validation-fails-before-dispatch-and-redacts-validator-message", async () =>
{
    var probe = new Probe();
    var context = Context("{\"tenant\":\"A\",\"id\":0}", probe: probe);
    await OperationHttpAdapter.ExecuteAsync(context, binding);
    Assert(context.Response.StatusCode == 400 && probe.Calls == 0);
    Assert(Body(context).Contains("validation.failed") && !Body(context).Contains("unsafe"));
});
foreach (var (name, json) in new (string, string)[]
{
    ("malformed", "{"), ("null-root", "null"), ("array-root", "[]"),
    ("duplicate", "{\"tenant\":\"A\",\"id\":1,\"id\":2}"),
    ("escaped-duplicate", "{\"tenant\":\"A\",\"id\":1,\"\\u0069d\":2}"),
    ("wrong-type", "{\"tenant\":\"A\",\"id\":\"1\"}"),
    ("unknown", "{\"tenant\":\"A\",\"id\":1,\"admin\":true}"),
    ("case-ambiguity", "{\"tenant\":\"A\",\"id\":1,\"Id\":2}"),
    ("missing-constructor-field", "{\"id\":1}"),
    ("null-nonnullable", "{\"tenant\":null,\"id\":1}"),
    ("trailing", "{\"tenant\":\"A\",\"id\":1} {}"),
    ("trailing-comma", "{\"tenant\":\"A\",\"id\":1,}"),
    ("comments", "{/*comment*/\"tenant\":\"A\",\"id\":1}"),
    ("invalid-surrogate", "{\"tenant\":\"\\uD800\",\"id\":1}"),
    ("invalid-low-surrogate", "{\"tenant\":\"\\uDC00\",\"id\":1}"),
    ("invalid-name-surrogate", "{\"\\uD800\":\"A\",\"id\":1}")
})
    await Check("strict-json-" + name, async () =>
    {
        var probe = new Probe();
        var context = Context(json, probe: probe);
        await OperationHttpAdapter.ExecuteAsync(context, binding);
        Assert(context.Response.StatusCode == 400 && probe.Calls == 0, Body(context));
    });
await Check("declared-too-large-rejects-before-reading", async () =>
{
    var probe = new Probe();
    var context = Context(probe: probe);
    context.Request.ContentLength = OperationHttpAdapter.MaximumBodyBytes + 1;
    await OperationHttpAdapter.ExecuteAsync(context, binding);
    Assert(context.Response.StatusCode == 413 && probe.Calls == 0 && context.Request.Body.Position == 0);
});
await Check("invalid-utf8-json-is-validation-failure", async () =>
{
    var probe = new Probe();
    var context = Context(probe: probe);
    context.Request.Body = new MemoryStream([.. Encoding.UTF8.GetBytes("{\"tenant\":\""), 0xff, .. Encoding.UTF8.GetBytes("\",\"id\":1}")]);
    await OperationHttpAdapter.ExecuteAsync(context, binding);
    Assert(context.Response.StatusCode == 400 && probe.Calls == 0);
});
await Check("nested-duplicate-json-is-rejected-before-typed-conversion", async () =>
{
    var probe = new Probe();
    var context = Context("{\"tenant\":\"A\",\"id\":1,\"other\":{\"a\":1,\"a\":2}}", probe: probe);
    await OperationHttpAdapter.ExecuteAsync(context, binding);
    Assert(context.Response.StatusCode == 400 && probe.Calls == 0);
});
await Check("escaped-lone-surrogate-dictionary-key-is-rejected", async () =>
{
    var dictionaryBinding = new OperationBinding<DictionaryInput, Output>("catalog.get", _ => null);
    foreach (var key in new[] { "\\uD800", "\\uDC00" })
    {
        var probe = new Probe();
        var context = Context("{\"values\":{\"" + key + "\":\"x\"}}", probe: probe);
        await OperationHttpAdapter.ExecuteAsync(context, dictionaryBinding);
        Assert(context.Response.StatusCode == 400 && probe.Calls == 0, $"status={context.Response.StatusCode} calls={probe.Calls} body={Body(context)}");
    }
});
await Check("valid-surrogate-pair-and-literal-backslash-u-dictionary-keys-are-accepted", async () =>
{
    var dictionaryBinding = new OperationBinding<DictionaryInput, Output>("catalog.get", _ => null);
    foreach (var key in new[] { "\\uD83D\\uDE00", "\\\\uD800" })
    {
        var probe = new Probe();
        var context = Context("{\"values\":{\"" + key + "\":\"x\"}}", probe: probe);
        await OperationHttpAdapter.ExecuteAsync(context, dictionaryBinding);
        Assert(context.Response.StatusCode == 200 && probe.Calls == 1, Body(context));
    }
});
await Check("escaped-lone-surrogate-raw-json-property-is-rejected", async () =>
{
    var rawBinding = new OperationBinding<RawInput, Output>("catalog.get", _ => null);
    foreach (var key in new[] { "\\uD800", "\\uDC00" })
    {
        var probe = new Probe();
        var context = Context("{\"extra\":{\"" + key + "\":1}}", probe: probe);
        await OperationHttpAdapter.ExecuteAsync(context, rawBinding);
        Assert(context.Response.StatusCode == 400 && probe.Calls == 0, $"status={context.Response.StatusCode} calls={probe.Calls} body={Body(context)}");
    }
});
await Check("streamed-too-large-without-content-length-rejects", async () =>
{
    var probe = new Probe();
    var context = Context(new string(' ', OperationHttpAdapter.MaximumBodyBytes + 1), probe: probe);
    context.Request.Body = new FragmentedStream(((MemoryStream)context.Request.Body).ToArray(), 7);
    await OperationHttpAdapter.ExecuteAsync(context, binding);
    Assert(context.Response.StatusCode == 413 && probe.Calls == 0);
});
await Check("exact-body-boundary-accepts-valid-json", async () =>
{
    var probe = new Probe();
    const string json = "{\"tenant\":\"A\",\"id\":1}";
    var context = Context(json.PadRight(OperationHttpAdapter.MaximumBodyBytes), probe: probe);
    await OperationHttpAdapter.ExecuteAsync(context, binding);
    Assert(context.Response.StatusCode == 200 && probe.Calls == 1);
});
foreach (var contentType in new[] { "text/plain", "application/json; charset=utf-16", "broken" })
    await Check("unsupported-content-type-" + contentType, async () =>
    {
        var context = Context();
        context.Request.ContentType = contentType;
        await OperationHttpAdapter.ExecuteAsync(context, binding);
        Assert(context.Response.StatusCode == 415 && context.Request.Body.Position == 0);
    });
await Check("encoded-body-is-not-implicitly-decompressed", async () =>
{
    var context = Context(); context.Request.Headers.ContentEncoding = "gzip";
    await OperationHttpAdapter.ExecuteAsync(context, binding);
    Assert(context.Response.StatusCode == 415);
});
await Check("wrong-method-denies-before-body-read", async () =>
{
    var context = Context(); context.Request.Method = "GET";
    await OperationHttpAdapter.ExecuteAsync(context, binding);
    Assert(context.Response.StatusCode == 405 && context.Response.Headers.Allow == "POST" && context.Request.Body.Position == 0);
});
await Check("denial-has-generic-problem-and-no-owner-detail", async () =>
{
    var probe = new Probe { ResultError = new OperationError("access.denied", "secret tenant B owns resource") };
    var context = Context(probe: probe);
    await OperationHttpAdapter.ExecuteAsync(context, binding);
    Assert(context.Response.StatusCode == 403 && Body(context).Contains("Access denied.") && !Body(context).Contains("secret"));
});
await Check("intentional-business-errors-have-explicit-status", async () =>
{
    foreach (var (code, status) in new[] { ("resource.not_found", 404), ("resource.conflict", 409), ("catalog.failure", 422), ("validation.failed", 400) })
    {
        var context = Context(probe: new Probe { ResultError = new OperationError(code, "Safe business message") });
        await OperationHttpAdapter.ExecuteAsync(context, binding);
        Assert(context.Response.StatusCode == status && Body(context).Contains(code));
    }
});
await Check("unexpected-fault-is-safe-internal-error", async () =>
{
    var context = Context(probe: new Probe { Fault = new InvalidOperationException("private infrastructure detail") });
    await OperationHttpAdapter.ExecuteAsync(context, binding);
    Assert(context.Response.StatusCode == 500 && Body(context).Contains("internal.error") && !Body(context).Contains("private"));
});
await Check("trusted-validator-fault-is-not-client-validation-error", async () =>
{
    var probe = new Probe();
    var context = Context(probe: probe);
    var faultyBinding = new OperationBinding<Input, Output>("catalog.get", _ => throw new InvalidOperationException("private validator fault"));
    await OperationHttpAdapter.ExecuteAsync(context, faultyBinding);
    Assert(context.Response.StatusCode == 500 && probe.Calls == 0 && !Body(context).Contains("private"));
});
await Check("unrelated-operation-cancelled-is-infrastructure-fault", async () =>
{
    var context = Context(probe: new Probe { Fault = new OperationCanceledException("private timeout") });
    await OperationHttpAdapter.ExecuteAsync(context, binding);
    Assert(context.Response.StatusCode == 500 && Body(context).Contains("internal.error"));
});
await Check("missing-dispatcher-is-safe-server-configuration-fault", async () =>
{
    var context = Context();
    context.RequestServices = new ServiceCollection().BuildServiceProvider();
    await OperationHttpAdapter.ExecuteAsync(context, binding);
    Assert(context.Response.StatusCode == 500 && !Body(context).Contains("Service"));
});
await Check("request-cancellation-token-reaches-dispatch-and-propagates", async () =>
{
    using var source = new CancellationTokenSource();
    var probe = new Probe { DuringCall = () => source.Cancel() };
    var context = Context(probe: probe); context.RequestAborted = source.Token;
    try { await OperationHttpAdapter.ExecuteAsync(context, binding); throw new Exception("cancellation swallowed"); }
    catch (OperationCanceledException) { }
    Assert(probe.LastToken == source.Token && probe.Calls == 1 && Body(context) == "");
});
await Check("pre-cancelled-request-does-zero-work", async () =>
{
    using var source = new CancellationTokenSource(); source.Cancel();
    var probe = new Probe(); var context = Context(probe: probe); context.RequestAborted = source.Token;
    try { await OperationHttpAdapter.ExecuteAsync(context, binding); throw new Exception("cancellation swallowed"); }
    catch (OperationCanceledException) { }
    Assert(probe.Calls == 0 && context.Request.Body.Position == 0);
});
await Check("cancellation-during-body-read-does-no-dispatch", async () =>
{
    using var source = new CancellationTokenSource();
    var probe = new Probe(); var context = Context(probe: probe); context.RequestAborted = source.Token;
    context.Request.Body = new CancelDuringReadStream(source);
    try { await OperationHttpAdapter.ExecuteAsync(context, binding); throw new Exception("cancellation swallowed"); }
    catch (OperationCanceledException) { }
    Assert(probe.Calls == 0 && Body(context) == "");
});
await Check("mapping-is-lazy-and-requires-authorization-metadata", async () =>
{
    var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [], EnvironmentName = "Production" });
    builder.Services.AddAuthorization();
    await using var app = builder.Build();
    var mappings = (IEndpointRouteBuilder)app;
    Assert(mappings.DataSources.Sum(source => source.Endpoints.Count) == 0);
    app.MapOperation("/api/catalog/get", binding);
    var endpoint = mappings.DataSources.SelectMany(source => source.Endpoints).Single();
    Assert(endpoint.Metadata.GetMetadata<IAuthorizeData>() is not null);
    Assert(endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods.SequenceEqual(["POST"]) == true);
    Assert(!app.Services.GetServices<ITransportOperationDispatcher>().Any());
});

var report = new { suite = "Http.ContractTests", total = checks.Count, passed = checks.Count - failures, failed = failures, checks };
var reportJson = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
Console.WriteLine(reportJson);
var output = args.FirstOrDefault(arg => arg.StartsWith("--output=", StringComparison.Ordinal))?[9..];
if (output is null && args.Length == 2 && args[0] == "--output") output = args[1];
if (output is not null) await File.WriteAllTextAsync(output, reportJson);
return failures == 0 ? 0 : 1;

public sealed record Input(string Tenant, int Id);
public sealed record DictionaryInput(Dictionary<string, string> Values);
public sealed record RawInput(JsonElement Extra);
public sealed record Output(string Label);
public sealed class Probe : ITransportOperationDispatcher
{
    public int Calls { get; private set; }
    public Input? LastInput { get; private set; }
    public string? LastOperation { get; private set; }
    public OperationContext? LastContext { get; private set; }
    public CancellationToken LastToken { get; private set; }
    public OperationError? ResultError { get; init; }
    public Exception? Fault { get; init; }
    public Action? DuringCall { get; init; }
    public ValueTask<OperationResult<TResponse>> DispatchAsync<TRequest, TResponse>(HttpContext context, string operationId,
        TRequest request, OperationContext operationContext, CancellationToken cancellationToken)
    {
        Calls++; LastInput = request as Input; LastOperation = operationId; LastContext = operationContext; LastToken = cancellationToken;
        DuringCall?.Invoke();
        if (Fault is not null) throw Fault;
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(ResultError is null
            ? OperationResult<TResponse>.Success((TResponse)(object)new Output("allowed"))
            : OperationResult<TResponse>.Failure(ResultError));
    }
}
public sealed class FragmentedStream(byte[] bytes, int fragmentSize) : MemoryStream(bytes)
{
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        => base.ReadAsync(buffer[..Math.Min(fragmentSize, buffer.Length)], cancellationToken);
}
public sealed class CancelDuringReadStream(CancellationTokenSource source) : MemoryStream
{
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        source.Cancel();
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(0);
    }
}
