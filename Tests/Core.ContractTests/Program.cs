using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Cehpalon.Engine.Core;

var checks = new List<object>();
var failures = 0;
var context = new OperationContext("core-contracts");

async Task Check(string name, Func<Task> test)
{
    try
    {
        await test();
        checks.Add(new { name, passed = true });
    }
    catch (Exception error)
    {
        failures++;
        checks.Add(new { name, passed = false, detail = error.ToString() });
    }
}
Task Sync(Action test) { test(); return Task.CompletedTask; }
void Assert(bool condition, string message = "Assertion failed")
{
    if (!condition) throw new InvalidOperationException(message);
}
T Throws<T>(Action action) where T : Exception
{
    try { action(); } catch (T error) { return error; }
    throw new InvalidOperationException("Expected " + typeof(T).Name);
}
async Task<T> ThrowsAsync<T>(Func<Task> action) where T : Exception
{
    try { await action(); } catch (T error) { return error; }
    throw new InvalidOperationException("Expected " + typeof(T).Name);
}
OperationDispatcher Dispatcher(Func<IOperationHandler<int, int>> factory,
    Action<OperationDispatchCompletion>? observer = null)
    => new(new OperationRegistryBuilder().Register("test.run", factory).Build(), observer);

bool IsDispatchDuration(Instrument instrument) => instrument.Meter.Name == "Cehpalon.Engine.Core" &&
    instrument.Name == "cehpalon.operation.dispatch.duration";

async Task CheckFirstPublication(bool failPublication)
{
    var factories = 0;
    var cleanups = 0;
    var publications = 0;
    var unpublications = 0;
    var measurements = 0;
    var dispatcher = Dispatcher(() =>
    {
        Interlocked.Increment(ref factories);
        return new Handler(cleanup: () => Interlocked.Increment(ref cleanups));
    });

    async Task Dispatch(int value) =>
        Assert((await dispatcher.DispatchAsync<int, int>("test.run", value, context)).Value == value);

    using (var observer = new MeterListener())
    {
        observer.InstrumentPublished = (instrument, current) =>
        {
            if (!IsDispatchDuration(instrument)) return;
            publications++;
            current.EnableMeasurementEvents(instrument);
            if (failPublication) throw new InvalidOperationException("Publishing listener failed.");

            Dispatch(2).GetAwaiter().GetResult();
            Task.Run(() => Dispatch(3)).GetAwaiter().GetResult();
        };
        observer.MeasurementsCompleted = (instrument, _) =>
        {
            if (!IsDispatchDuration(instrument) || !failPublication) return;
            unpublications++;
            throw new InvalidOperationException("Completing listener failed.");
        };
        observer.SetMeasurementEventCallback<double>((_, _, _, _) => Interlocked.Increment(ref measurements));
        observer.Start();
        Assert(publications == 0, "The probe must start before the instrument exists.");
        await Dispatch(1);
        var expectedCalls = failPublication ? 1 : 3;
        Assert(factories == expectedCalls && cleanups == expectedCalls);
        Assert(publications == 1 && unpublications == (failPublication ? 1 : 0));
        Assert(measurements == (failPublication ? 0 : 1), "Only the owning publication attempt can be timed.");
    }

    if (failPublication)
    {
        using var laterObserver = new MeterListener();
        laterObserver.InstrumentPublished = (instrument, _) =>
        {
            if (IsDispatchDuration(instrument)) publications++;
        };
        laterObserver.Start();
        await Dispatch(4);
        Assert(factories == 2 && cleanups == 2 && publications == 1,
            "A failed publication must neither strand the original meter nor disable later dispatch.");
    }
}

const string publicationFault = "--dispatch-publication-fault-probe";
const string publicationReentry = "--dispatch-publication-reentry-probe";
const string publicationTrace = "--dispatch-publication-trace-probe";
if (args is [publicationFault] or [publicationReentry] or [publicationTrace])
{
    try
    {
        if (args[0] == publicationTrace)
        {
            using var entry = new Activity("dispatch-entry").SetIdFormat(ActivityIdFormat.W3C).Start();
            using var replacement = new Activity("instrument-publication").SetIdFormat(ActivityIdFormat.W3C);
            OperationDispatchCompletion? record = null;
            var publications = 0;
            var dispatcher = Dispatcher(() => new Handler((request, _, _) =>
            {
                Assert(ReferenceEquals(Activity.Current, replacement));
                return ValueTask.FromResult(OperationResult<int>.Success(request));
            }), completion => record = completion);
            using var listener = new MeterListener();
            listener.InstrumentPublished = (instrument, _) =>
            {
                if (!IsDispatchDuration(instrument)) return;
                publications++;
                replacement.Start();
            };
            listener.Start();
            Assert(publications == 0, "The probe must start before the instrument exists.");
            Assert((await dispatcher.DispatchAsync<int, int>("test.run", 1, context)).Value == 1);
            Assert(publications == 1 && record?.TraceId == entry.TraceId && record?.SpanId == entry.SpanId);
        }
        else
        {
            await CheckFirstPublication(args[0] == publicationFault);
        }
        return 0;
    }
    catch (Exception error)
    {
        Console.Error.WriteLine(error);
        return 1;
    }
}

async Task RunPublicationProbe(string argument)
{
    var launcher = Environment.ProcessPath ?? throw new InvalidOperationException("The test launcher path is missing.");
    var start = new ProcessStartInfo(launcher) { RedirectStandardError = true };
    if (string.Equals(Path.GetFileNameWithoutExtension(launcher), "dotnet", StringComparison.OrdinalIgnoreCase))
        start.ArgumentList.Add(typeof(Handler).Assembly.Location);
    start.ArgumentList.Add(argument);
    using var child = Process.Start(start) ?? throw new InvalidOperationException("Could not start the publication probe.");
    var error = child.StandardError.ReadToEndAsync();
    try
    {
        await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
    }
    catch (TimeoutException)
    {
        if (!child.HasExited) child.Kill(entireProcessTree: true);
        await child.WaitForExitAsync();
        throw;
    }
    Assert(child.ExitCode == 0, $"Publication probe exited with {child.ExitCode}. {await error}");
}

await Check("dispatch-first-publication-fault-preserves-ownership", () => RunPublicationProbe(publicationFault));
await Check("dispatch-first-publication-allows-reentrant-and-concurrent-callers", () => RunPublicationProbe(publicationReentry));
await Check("dispatch-completion-captures-entry-before-instrument-publication", () => RunPublicationProbe(publicationTrace));

await Check("dispatch-duration-records-only-bounded-outcomes", async () =>
{
    var outcomes = new List<string>();
    var validMeasurements = true;
    var success = Dispatcher(() => new Handler((_, _, _) =>
        ValueTask.FromResult(OperationResult<int>.Success(1))));
    Assert((await success.DispatchAsync<int, int>("test.run", 0, context)).Value == 1);
    var callbacks = 0;
    Instrument? observed = null;
    using var listener = new MeterListener();
    listener.InstrumentPublished = (instrument, _) =>
    {
        if (IsDispatchDuration(instrument))
        {
            validMeasurements &= instrument.Unit == "s";
            observed = instrument;
        }
    };
    listener.SetMeasurementEventCallback<double>((_, seconds, tags, _) =>
    {
        validMeasurements &= double.IsFinite(seconds) && seconds >= 0 &&
            tags.Length == 1 && tags[0].Key == "outcome";
        callbacks++;
        if (tags.Length == 1 && tags[0].Value is string outcome)
            outcomes.Add(outcome);
        throw new InvalidOperationException("Listener failure");
    });
    listener.Start();
    Assert(observed is not null, "Dispatch histogram was not published.");
    listener.EnableMeasurementEvents(observed!);
    Assert(observed?.Enabled == true, "Dispatch histogram was not enabled.");

    Assert((await success.DispatchAsync<int, int>("test.run", 0, context)).Value == 1);

    var failure = Dispatcher(() => new Handler((_, _, _) =>
        ValueTask.FromResult(OperationResult<int>.Failure(new OperationError("test.failure", "private")))));
    Assert(!(await failure.DispatchAsync<int, int>("test.run", 0, context)).IsSuccess);

    using var cts = new CancellationTokenSource();
    var canceled = Dispatcher(() => new Handler((_, _, token) =>
    {
        cts.Cancel();
        return ValueTask.FromCanceled<OperationResult<int>>(token);
    }));
    var cancellation = await ThrowsAsync<OperationCanceledException>(async () =>
        await canceled.DispatchAsync<int, int>("test.run", 0, context, cts.Token));
    Assert(cancellation.CancellationToken == cts.Token);

    var expected = new InvalidOperationException("private factory exception");
    var fault = Dispatcher(() => throw expected);
    var actual = await ThrowsAsync<InvalidOperationException>(async () =>
        await fault.DispatchAsync<int, int>("test.run", 0, context));
    Assert(ReferenceEquals(expected, actual));
    var cleanupFault = Dispatcher(() => new Handler(cleanup: () => throw expected));
    var cleanupError = await ThrowsAsync<InvalidOperationException>(async () =>
        await cleanupFault.DispatchAsync<int, int>("test.run", 0, context));
    Assert(ReferenceEquals(expected, cleanupError));

    using var cleanupCancellation = new CancellationTokenSource();
    var expectedCleanupCancellation = new OperationCanceledException(cleanupCancellation.Token);
    var canceledCleanup = Dispatcher(() => new Handler(cleanup: () =>
    {
        cleanupCancellation.Cancel();
        throw expectedCleanupCancellation;
    }));
    var canceledCleanupError = await ThrowsAsync<OperationCanceledException>(async () =>
        await canceledCleanup.DispatchAsync<int, int>("test.run", 0, context, cleanupCancellation.Token));
    Assert(ReferenceEquals(expectedCleanupCancellation, canceledCleanupError));

    var cleanupGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var asynchronousHandler = new AsyncHandler(() => new ValueTask(cleanupGate.Task));
    var pending = Dispatcher(() => asynchronousHandler).DispatchAsync<int, int>("test.run", 14, context);
    OperationResult<int> afterCleanup;
    try
    {
        Assert(asynchronousHandler.AsyncDisposeCalls == 1 && !pending.IsCompleted && callbacks == 6,
            "The measurement must wait for cleanup completion.");
    }
    finally
    {
        cleanupGate.TrySetResult();
        afterCleanup = await pending;
    }
    Assert(afterCleanup.Value == 14);

    Assert(!(await success.DispatchAsync<int, int>("unknown", 0, context)).IsSuccess);
    Assert(!(await success.DispatchAsync<string, int>("test.run", "mismatch", context)).IsSuccess);
    await ThrowsAsync<OperationCanceledException>(async () =>
        await success.DispatchAsync<int, int>("test.run", 0, context, cts.Token));
    Assert(validMeasurements && outcomes.SequenceEqual(new[] { "success", "failure", "canceled", "fault", "fault", "fault", "success" }),
        $"Valid measurements: {validMeasurements}. Callbacks: {callbacks}. Outcomes: {string.Join(", ", outcomes)}.");
});

await Check("dispatch-completion-uses-fixed-projection-and-optional-w3c-ids", async () =>
{
    var completions = new List<OperationDispatchCompletion>();
    var registry = new OperationRegistryBuilder().Register<int, int>("test.run", () => new Handler()).Build();
    var observed = new OperationDispatcher(registry, completions.Add);
    var privateContext = new OperationContext("private-correlation",
        new Dictionary<string, string> { ["private-key"] = "private-value" });
    Assert((await new OperationDispatcher(registry).DispatchAsync<int, int>("test.run", 1, privateContext)).Value == 1);
    Assert((await new OperationDispatcher(registry, null).DispatchAsync<int, int>("test.run", 2, privateContext)).Value == 2);
    Assert(completions.Count == 0 && Activity.Current is null);
    await observed.DispatchAsync<int, int>("test.run", 3, privateContext);
    using var hierarchical = new Activity("private-activity").SetIdFormat(ActivityIdFormat.Hierarchical).Start();
    hierarchical.AddBaggage("private-baggage", "private-value");
    await observed.DispatchAsync<int, int>("test.run", 4, privateContext);
    Assert(ReferenceEquals(Activity.Current, hierarchical));
    Assert(completions.Count == 2 && completions.All(record => record.Outcome == OperationDispatchOutcome.Success &&
        record.TraceId is null && record.SpanId is null && record.Duration >= TimeSpan.Zero));
    Assert(OperationDispatchCompletion.EventName == "cehpalon.operation.dispatch.completed");
    var fields = typeof(OperationDispatchCompletion).GetProperties().Select(property => property.Name).Order();
    Assert(fields.SequenceEqual(new[] { "Duration", "Outcome", "SpanId", "TraceId" }));
});

await Check("dispatch-completion-only-observes-validated-registered-invocations", async () =>
{
    var factoryCalls = 0;
    var callbacks = 0;
    var observed = Dispatcher(() => { factoryCalls++; return new Handler(); }, _ => callbacks++);
    using var canceled = new CancellationTokenSource();
    canceled.Cancel();
    await ThrowsAsync<ArgumentException>(() => observed.DispatchAsync<int, int>("Bad", 0, context).AsTask());
    await ThrowsAsync<ArgumentNullException>(() => observed.DispatchAsync<int, int>("test.run", 0, null!).AsTask());
    await ThrowsAsync<OperationCanceledException>(() => observed.DispatchAsync<int, int>("test.run", 0, context, canceled.Token).AsTask());
    Assert((await observed.DispatchAsync<int, int>("unknown", 0, context)).Error.Code == "operation.not_found");
    Assert((await observed.DispatchAsync<string, int>("test.run", "mismatch", context)).Error.Code == "operation.contract_mismatch");
    Assert(factoryCalls == 0 && callbacks == 0);
});

await Check("dispatch-completion-observes-bounded-outcomes-without-changing-results", async () =>
{
    var outcomes = new List<OperationDispatchOutcome>();
    var observerError = new InvalidOperationException("observer fault");
    OperationDispatcher Observe(Func<IOperationHandler<int, int>> factory) => Dispatcher(factory, completion =>
    {
        outcomes.Add(completion.Outcome);
        throw observerError;
    });

    Assert((await Observe(() => new Handler()).DispatchAsync<int, int>("test.run", 8, context)).Value == 8);
    var expectedFailure = new OperationError("test.failure", "Private detail.");
    var failure = new Handler((_, _, _) => ValueTask.FromResult(OperationResult<int>.Failure(expectedFailure)));
    Assert(ReferenceEquals((await Observe(() => failure).DispatchAsync<int, int>("test.run", 1, context)).Error, expectedFailure));

    using var cancellation = new CancellationTokenSource();
    var expectedCancellation = new OperationCanceledException(cancellation.Token);
    var canceled = new Handler((_, _, _) => { cancellation.Cancel(); throw expectedCancellation; });
    var actualCancellation = await ThrowsAsync<OperationCanceledException>(() =>
        Observe(() => canceled).DispatchAsync<int, int>("test.run", 0, context, cancellation.Token).AsTask());
    Assert(ReferenceEquals(actualCancellation, expectedCancellation));

    var execution = new ApplicationException("execution fault");
    var executionFault = new Handler((_, _, _) => throw execution);
    Assert(ReferenceEquals(await ThrowsAsync<ApplicationException>(() =>
        Observe(() => executionFault).DispatchAsync<int, int>("test.run", 0, context).AsTask()), execution));

    using var cleanupCancellation = new CancellationTokenSource();
    var cleanup = new OperationCanceledException(cleanupCancellation.Token);
    var cleanupFault = new Handler(cleanup: () => { cleanupCancellation.Cancel(); throw cleanup; });
    Assert(ReferenceEquals(await ThrowsAsync<OperationCanceledException>(() =>
        Observe(() => cleanupFault).DispatchAsync<int, int>("test.run", 0, context, cleanupCancellation.Token).AsTask()), cleanup));

    var bothFault = new Handler((_, _, _) => throw execution, () => throw cleanup);
    var aggregate = await ThrowsAsync<AggregateException>(() =>
        Observe(() => bothFault).DispatchAsync<int, int>("test.run", 0, context).AsTask());
    Assert(aggregate.InnerExceptions.SequenceEqual(new Exception[] { execution, cleanup }));
    Assert(new[] { failure, canceled, executionFault, cleanupFault, bothFault }.All(handler => handler.DisposeCalls == 1));
    Assert(outcomes.SequenceEqual(new[] { OperationDispatchOutcome.Success, OperationDispatchOutcome.Failure,
        OperationDispatchOutcome.Canceled, OperationDispatchOutcome.Fault, OperationDispatchOutcome.Fault,
        OperationDispatchOutcome.Fault }));
});

await Check("dispatch-completion-waits-for-cleanup-and-ends-its-clock-before-callback", async () =>
{
    var cleanupRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var cleanupStarted = 0L;
    var cleanupCompleted = 0L;
    var callbackStarted = 0L;
    OperationDispatchCompletion? observed = null;
    var handler = new AsyncHandler(async () =>
    {
        cleanupStarted = Stopwatch.GetTimestamp();
        await cleanupRelease.Task;
        cleanupCompleted = Stopwatch.GetTimestamp();
    });
    var dispatcher = Dispatcher(() => handler, completion =>
    {
        callbackStarted = Stopwatch.GetTimestamp();
        observed = completion;
    });
    var beforeDispatch = Stopwatch.GetTimestamp();
    var pending = dispatcher.DispatchAsync<int, int>("test.run", 9, context);
    try
    {
        Assert(!pending.IsCompleted && handler.AsyncDisposeCalls == 1 && observed is null);
    }
    finally
    {
        cleanupRelease.TrySetResult();
    }
    Assert((await pending.AsTask().WaitAsync(TimeSpan.FromSeconds(5))).Value == 9);
    Assert(observed is { } record && record.Duration >= Stopwatch.GetElapsedTime(cleanupStarted, cleanupCompleted) &&
        record.Duration <= Stopwatch.GetElapsedTime(beforeDispatch, callbackStarted) &&
        callbackStarted >= cleanupCompleted && handler.SyncDisposeCalls == 0);
});

await Check("dispatch-completion-suppresses-callback-originated-flow-across-awaits", async () =>
{
    var callbacks = 0;
    var nestedCallbacks = 0;
    var nestedCleanups = 0;
    var nestedResult = 0;
    var nested = Dispatcher(() => new Handler(async (request, _, _) =>
    {
        await Task.Yield();
        return OperationResult<int>.Success(request);
    }, () => Interlocked.Increment(ref nestedCleanups)), _ => Interlocked.Increment(ref nestedCallbacks));
    var outer = Dispatcher(() => new Handler(), _ =>
    {
        callbacks++;
        nestedResult = Task.Run(async () => (await nested.DispatchAsync<int, int>("test.run", 42, context)).Value)
            .WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        throw new ApplicationException("observer fault after nested dispatch");
    });
    Assert((await outer.DispatchAsync<int, int>("test.run", 1, context)).Value == 1);
    Assert(callbacks == 1 && nestedResult == 42 && nestedCleanups == 1 && nestedCallbacks == 0);
    Assert((await nested.DispatchAsync<int, int>("test.run", 2, context)).Value == 2);
    Assert(nestedCallbacks == 1 && nestedCleanups == 2);
});

await Check("dispatch-completion-captures-independent-w3c-contexts-during-overlap", async () =>
{
    var bothEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var entered = 0;
    var records = new System.Collections.Concurrent.ConcurrentQueue<OperationDispatchCompletion>();
    var dispatcher = Dispatcher(() => new Handler(async (request, _, _) =>
    {
        if (Interlocked.Increment(ref entered) == 2) bothEntered.TrySetResult();
        await release.Task;
        return OperationResult<int>.Success(request);
    }), records.Enqueue);

    async Task<(ActivityTraceId, ActivitySpanId)> Invoke(int request)
    {
        using var activity = new Activity("private-dispatch-context").SetIdFormat(ActivityIdFormat.W3C).Start();
        var ids = (activity.TraceId, activity.SpanId);
        Assert((await dispatcher.DispatchAsync<int, int>("test.run", request, context)).Value == request);
        Assert(ReferenceEquals(Activity.Current, activity));
        return ids;
    }

    var first = Task.Run(() => Invoke(1));
    var second = Task.Run(() => Invoke(2));
    try
    {
        await bothEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert(records.IsEmpty);
    }
    finally
    {
        release.TrySetResult();
    }
    var expected = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
    var actual = records.ToArray();
    Assert(expected[0].Item1 != expected[1].Item1 && actual.Length == 2 &&
        expected.All(ids => actual.Count(record => record.TraceId == ids.Item1 && record.SpanId == ids.Item2) == 1));
});

const string traceId = "4bf92f3577b34da6a3ce929d0e0e4736";
const string parentSpanId = "00f067aa0ba902b7";
const string validTraceParent = "00-" + traceId + "-" + parentSpanId + "-01";

foreach (var (flagsHex, flags) in new[]
    { ("00", 0), ("01", 1), ("09", 0x09), ("9f", 0x9f), ("af", 0xaf), ("ff", 0xff) })
{
    await Check($"traceparent-accepts-55-characters-with-flags-{flagsHex}", () => Sync(() =>
    {
        var value = validTraceParent[..^2] + flagsHex;
        Assert(value.Length == 55);
        var parsed = TraceParent.ParseOrNull([value]);
        Assert(parsed is { } result &&
            result.TraceId == ActivityTraceId.CreateFromString(traceId) &&
            result.ParentSpanId == ActivitySpanId.CreateFromString(parentSpanId) && result.Flags == flags);
    }));
}

Task RejectTraceParent(string reason, params string?[] values) =>
    Check($"traceparent-rejects-{reason}", () => Sync(() => Assert(TraceParent.ParseOrNull(values) is null)));

await RejectTraceParent("absent", []);
await RejectTraceParent("null-value", [null]);
await RejectTraceParent("empty-value", "");
await RejectTraceParent("duplicate-values", validTraceParent, validTraceParent);
await RejectTraceParent("multiple-values-even-if-one-is-empty", "", validTraceParent);
await RejectTraceParent("comma-combined-values", validTraceParent + "," + validTraceParent);
await RejectTraceParent("54-characters", validTraceParent[..^1]);
await RejectTraceParent("56-characters", validTraceParent + "0");
await RejectTraceParent("uppercase-trace-id", $"00-{traceId.ToUpperInvariant()}-{parentSpanId}-01");
await RejectTraceParent("uppercase-parent-id", $"00-{traceId}-{parentSpanId.ToUpperInvariant()}-01");
await RejectTraceParent("non-ascii-trace-id", $"00-{traceId[..3]}\uFF19{traceId[4..]}-{parentSpanId}-01");
await RejectTraceParent("non-ascii-parent-id", $"00-{traceId}-{parentSpanId[..1]}\uFF10{parentSpanId[2..]}-01");
await RejectTraceParent("uppercase-flags", validTraceParent[..^2] + "aF");
await RejectTraceParent("non-ascii-hex-lookalike", validTraceParent[..^1] + "\uFF11");
await RejectTraceParent("uppercase-high-flags-nibble", validTraceParent[..53] + "A" + validTraceParent[54..]);
await RejectTraceParent("non-ascii-high-flags-nibble", validTraceParent[..53] + "\uFF10" + validTraceParent[54..]);
await RejectTraceParent("non-hex-high-flags-nibble", validTraceParent[..53] + "g" + validTraceParent[54..]);
await RejectTraceParent("unsupported-version", "01" + validTraceParent[2..]);
await RejectTraceParent("invalid-version-ff", "ff" + validTraceParent[2..]);
await RejectTraceParent("zero-trace-id", $"00-{new string('0', 32)}-{parentSpanId}-01");
await RejectTraceParent("zero-parent-id", $"00-{traceId}-{new string('0', 16)}-01");
await RejectTraceParent("non-hex-flags", validTraceParent[..^1] + "g");
await RejectTraceParent("whitespace-padding", " " + validTraceParent);
await RejectTraceParent("trailing-space-at-55-characters", validTraceParent[..^1] + " ");
await RejectTraceParent("trailing-tab-at-55-characters", validTraceParent[..^1] + "\t");
await RejectTraceParent("trailing-space-at-56-characters", validTraceParent + " ");
await RejectTraceParent("trailing-tab-at-56-characters", validTraceParent + "\t");
foreach (var index in new[] { 2, 35, 52 })
    await RejectTraceParent($"bad-separator-at-{index}", validTraceParent[..index] + ":" + validTraceParent[(index + 1)..]);

await Check("traceparent-invalid-input-never-throws-or-yields-a-partial-result", () => Sync(() =>
{
    for (var index = 0; index < validTraceParent.Length; index++)
        Assert(TraceParent.ParseOrNull([validTraceParent[..index] + '\0' + validTraceParent[(index + 1)..]]) is null,
            $"Invalid character at offset {index} must be treated as absent.");
}));

await Check("canonical-id-grammar-and-boundaries", () => Sync(() =>
{
    foreach (var value in new[] { "a", "test.run-1_v2", new string('a', 128) }) Assert(OperationId.IsValid(value));
    foreach (var value in new string?[] { null, "", " ", "A", "aB", "0a", "a/b", "a:b", "á", "a\n", "a\0", new('a', 129) })
    {
        Assert(!OperationId.IsValid(value));
        Throws<ArgumentException>(() => OperationId.RequireValid(value));
    }
}));
await Check("attribute-validates-id-without-handler-activation", () => Sync(() =>
{
    Assert(new OperationAttribute("catalog.get").Id == "catalog.get");
    Throws<ArgumentException>(() => new OperationAttribute("Catalog.Get"));
}));
await Check("success-result-and-error-access-guard", () => Sync(() =>
{
    var result = OperationResult<int>.Success(7);
    Assert(result.IsSuccess && result.Value == 7);
    Throws<InvalidOperationException>(() => _ = result.Error);
}));
await Check("failure-result-and-value-access-guard", () => Sync(() =>
{
    var error = new OperationError("catalog.missing", "The item does not exist.");
    var result = OperationResult<int>.Failure(error);
    Assert(!result.IsSuccess && ReferenceEquals(result.Error, error));
    Throws<InvalidOperationException>(() => _ = result.Value);
    Throws<ArgumentNullException>(() => OperationResult<int>.Failure(null!));
}));
await Check("nullable-success-is-intentional", () => Sync(() => Assert(OperationResult<string?>.Success(null).Value is null)));
await Check("error-code-message-contract", () => Sync(() =>
{
    Throws<ArgumentException>(() => new OperationError("Invalid", "safe"));
    Throws<ArgumentException>(() => new OperationError("valid", " "));
    Throws<ArgumentException>(() => new OperationError("valid", new('a', 4097)));
}));
await Check("context-defensive-snapshot-and-readonly-view", () => Sync(() =>
{
    var original = new Dictionary<string, string> { ["source"] = "first" };
    var snapshot = new OperationContext("correlation", original);
    original["source"] = "changed";
    original["new"] = "added";
    Assert(snapshot.CorrelationId == "correlation" && snapshot.Metadata["source"] == "first" && snapshot.Metadata.Count == 1);
    Throws<NotSupportedException>(() => ((IDictionary<string, string>)snapshot.Metadata).Add("tamper", "no"));
}));
await Check("context-rejects-invalid-and-unbounded-metadata", () => Sync(() =>
{
    Throws<ArgumentException>(() => new OperationContext(" "));
    Throws<ArgumentException>(() => new OperationContext(new('a', 257)));
    Throws<ArgumentException>(() => new OperationContext("ok", new[] { KeyValuePair.Create("key", "first"), KeyValuePair.Create("key", "second") }));
    Throws<ArgumentException>(() => new OperationContext("ok", new[] { KeyValuePair.Create(" ", "value") }));
    Throws<ArgumentNullException>(() => new OperationContext("ok", new[] { KeyValuePair.Create("key", (string)null!) }));
    Throws<ArgumentException>(() => new OperationContext("ok", new[] { KeyValuePair.Create(new string('k', 129), "value") }));
    Throws<ArgumentException>(() => new OperationContext("ok", new[] { KeyValuePair.Create("key", new string('v', 4097)) }));
    Throws<ArgumentException>(() => new OperationContext("ok", Enumerable.Range(0, 65).Select(x => KeyValuePair.Create("key"+x, "value"))));
}));
await Check("registration-build-and-enumeration-invoke-no-factory", () => Sync(() =>
{
    var calls = 0;
    var registry = new OperationRegistryBuilder().Register<int, int>("test.run", () => { calls++; return new Handler(); }).Build();
    Assert(registry.OperationIds.SequenceEqual(new[] { "test.run" }) && calls == 0);
}));
await Check("registry-snapshot-is-independent-and-ordinally-ordered", () => Sync(() =>
{
    var builder = new OperationRegistryBuilder().Register<int, int>("z", () => new Handler());
    var old = builder.Build();
    builder.Register<int, int>("a", () => new Handler());
    var current = builder.Build();
    Assert(old.OperationIds.SequenceEqual(new[] { "z" }));
    Assert(current.OperationIds.SequenceEqual(new[] { "a", "z" }));
    Throws<NotSupportedException>(() => ((IList<string>)current.OperationIds)[0] = "tamper");
}));
await Check("duplicate-invalid-and-null-factory-registration-rejected", () => Sync(() =>
{
    var builder = new OperationRegistryBuilder().Register<int, int>("test.run", () => new Handler());
    Throws<ArgumentException>(() => builder.Register<int, int>("test.run", () => new Handler()));
    Throws<ArgumentException>(() => builder.Register<int, int>("TEST.run", () => new Handler()));
    Throws<ArgumentNullException>(() => builder.Register<int, int>("other", null!));
}));
await Check("unknown-operation-returns-known-error-with-zero-factory", async () =>
{
    var calls = 0;
    var dispatcher = Dispatcher(() => { calls++; return new Handler(); });
    var result = await dispatcher.DispatchAsync<int, int>("test.unknown", 1, context);
    Assert(!result.IsSuccess && result.Error.Code == "operation.not_found" && calls == 0);
});
await Check("request-type-mismatch-does-zero-factory-work", async () =>
{
    var calls = 0;
    var dispatcher = Dispatcher(() => { calls++; return new Handler(); });
    var result = await dispatcher.DispatchAsync<string, int>("test.run", "wrong", context);
    Assert(!result.IsSuccess && result.Error.Code == "operation.contract_mismatch" && calls == 0);
});
await Check("response-type-mismatch-does-zero-factory-work", async () =>
{
    var calls = 0;
    var dispatcher = Dispatcher(() => { calls++; return new Handler(); });
    var result = await dispatcher.DispatchAsync<int, string>("test.run", 1, context);
    Assert(!result.IsSuccess && result.Error.Code == "operation.contract_mismatch" && calls == 0);
});
await Check("invalid-context-and-id-before-factory", async () =>
{
    var calls = 0;
    var dispatcher = Dispatcher(() => { calls++; return new Handler(); });
    await ThrowsAsync<ArgumentNullException>(() => dispatcher.DispatchAsync<int, int>("test.run", 1, null!).AsTask());
    await ThrowsAsync<ArgumentException>(() => dispatcher.DispatchAsync<int, int>("Bad", 1, context).AsTask());
    Assert(calls == 0);
});
await Check("already-canceled-dispatch-before-factory", async () =>
{
    var calls = 0;
    using var cts = new CancellationTokenSource(); cts.Cancel();
    var error = await ThrowsAsync<OperationCanceledException>(() => Dispatcher(() => { calls++; return new Handler(); }).DispatchAsync<int, int>("test.run", 1, context, cts.Token).AsTask());
    Assert(calls == 0 && error.CancellationToken == cts.Token);
});
await Check("handler-receives-request-context-and-exact-token", async () =>
{
    using var cts = new CancellationTokenSource();
    var handler = new Handler((request, suppliedContext, token) =>
    {
        Assert(request == 9 && ReferenceEquals(suppliedContext, context) && token == cts.Token);
        return ValueTask.FromResult(OperationResult<int>.Success(request * 2));
    });
    var result = await Dispatcher(() => handler).DispatchAsync<int, int>("test.run", 9, context, cts.Token);
    Assert(result.Value == 18 && handler.DisposeCalls == 1);
});
await Check("factory-triggered-cancellation-skips-handler-and-disposes-owned-instance", async () =>
{
    using var cts = new CancellationTokenSource();
    var businessCalls = 0;
    var handler = new Handler((request, _, _) => { businessCalls++; return ValueTask.FromResult(OperationResult<int>.Success(request)); });
    var error = await ThrowsAsync<OperationCanceledException>(() => Dispatcher(() => { cts.Cancel(); return handler; }).DispatchAsync<int, int>("test.run", 1, context, cts.Token).AsTask());
    Assert(error.CancellationToken == cts.Token && businessCalls == 0 && handler.DisposeCalls == 1);
});
await Check("business-failure-is-preserved-and-handler-disposed", async () =>
{
    var expected = new OperationError("business.denied", "The operation was denied.");
    var handler = new Handler((_, _, _) => ValueTask.FromResult(OperationResult<int>.Failure(expected)));
    var result = await Dispatcher(() => handler).DispatchAsync<int, int>("test.run", 0, context);
    Assert(ReferenceEquals(expected, result.Error) && handler.DisposeCalls == 1);
});
await Check("unexpected-exception-propagates-and-handler-disposed", async () =>
{
    var expected = new ApplicationException("fixture exception");
    var handler = new Handler((_, _, _) => throw expected);
    var actual = await ThrowsAsync<ApplicationException>(() => Dispatcher(() => handler).DispatchAsync<int, int>("test.run", 1, context).AsTask());
    Assert(ReferenceEquals(expected, actual) && handler.DisposeCalls == 1);
});
await Check("cooperative-cancellation-propagates-and-disposes", async () =>
{
    using var cts = new CancellationTokenSource();
    var handler = new Handler((_, _, token) => { cts.Cancel(); token.ThrowIfCancellationRequested(); return ValueTask.FromResult(OperationResult<int>.Success(1)); });
    var actual = await ThrowsAsync<OperationCanceledException>(() => Dispatcher(() => handler).DispatchAsync<int, int>("test.run", 1, context, cts.Token).AsTask());
    Assert(actual.CancellationToken == cts.Token && handler.DisposeCalls == 1);
});
await Check("asynchronous-disposal-is-awaited-before-completion", async () =>
{
    var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var handler = new AsyncHandler(async () => { entered.SetResult(); await release.Task; });
    var dispatch = Dispatcher(() => handler).DispatchAsync<int, int>("test.run", 2, context).AsTask();
    await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
    Assert(!dispatch.IsCompleted && handler.AsyncDisposeCalls == 1);
    release.SetResult();
    Assert((await dispatch.WaitAsync(TimeSpan.FromSeconds(5))).Value == 2);
});
await Check("async-disposal-preferred-over-sync-without-double-dispose", async () =>
{
    var handler = new AsyncHandler(() => ValueTask.CompletedTask);
    await Dispatcher(() => handler).DispatchAsync<int, int>("test.run", 2, context);
    Assert(handler.AsyncDisposeCalls == 1 && handler.SyncDisposeCalls == 0);
});
await Check("faulty-null-result-rejected-and-handler-disposed", async () =>
{
    var handler = new Handler((_, _, _) => ValueTask.FromResult<OperationResult<int>>(null!));
    await ThrowsAsync<InvalidOperationException>(() => Dispatcher(() => handler).DispatchAsync<int, int>("test.run", 1, context).AsTask());
    Assert(handler.DisposeCalls == 1);
});
await Check("faulty-null-factory-result-rejected", async () =>
{
    await ThrowsAsync<InvalidOperationException>(() => Dispatcher(() => null!).DispatchAsync<int, int>("test.run", 1, context).AsTask());
});
await Check("factory-exception-is-not-converted-to-business-error", async () =>
{
    var expected = new ApplicationException("factory failure");
    var actual = await ThrowsAsync<ApplicationException>(() => Dispatcher(() => throw expected).DispatchAsync<int, int>("test.run", 1, context).AsTask());
    Assert(ReferenceEquals(actual, expected));
});
await Check("cleanup-only-failure-propagates", async () =>
{
    var expected = new ApplicationException("cleanup failure");
    var handler = new Handler(cleanup: () => throw expected);
    var actual = await ThrowsAsync<ApplicationException>(() => Dispatcher(() => handler).DispatchAsync<int, int>("test.run", 1, context).AsTask());
    Assert(ReferenceEquals(expected, actual) && handler.DisposeCalls == 1);
});
await Check("execution-and-cleanup-failures-both-preserved-in-order", async () =>
{
    var execution = new ApplicationException("execution");
    var cleanup = new ApplicationException("cleanup");
    var handler = new Handler((_, _, _) => throw execution, () => throw cleanup);
    var actual = await ThrowsAsync<AggregateException>(() => Dispatcher(() => handler).DispatchAsync<int, int>("test.run", 1, context).AsTask());
    Assert(actual.InnerExceptions.Count == 2 && ReferenceEquals(actual.InnerExceptions[0], execution) && ReferenceEquals(actual.InnerExceptions[1], cleanup) && handler.DisposeCalls == 1);
});
await Check("cancellation-plus-cleanup-failure-retains-cancellation-token", async () =>
{
    using var cts = new CancellationTokenSource();
    var handler = new Handler((_, _, token) => { cts.Cancel(); throw new OperationCanceledException(token); }, () => throw new ApplicationException("cleanup"));
    var actual = await ThrowsAsync<AggregateException>(() => Dispatcher(() => handler).DispatchAsync<int, int>("test.run", 1, context, cts.Token).AsTask());
    Assert(actual.InnerExceptions[0] is OperationCanceledException canceled && canceled.CancellationToken == cts.Token && handler.DisposeCalls == 1);
});
await Check("concurrent-dispatches-use-independent-owned-handlers", async () =>
{
    const int count = 64;
    var factoryCalls = 0; var entered = 0; var disposed = 0;
    var allEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var dispatcher = Dispatcher(() =>
    {
        Interlocked.Increment(ref factoryCalls);
        return new Handler(async (request, supplied, _) =>
        {
            Assert(ReferenceEquals(supplied, context));
            if (Interlocked.Increment(ref entered) == count) allEntered.TrySetResult();
            await allEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            return OperationResult<int>.Success(request);
        }, () => Interlocked.Increment(ref disposed));
    });
    var results = await Task.WhenAll(Enumerable.Range(0, count).Select(value => Task.Run(async () =>
        await dispatcher.DispatchAsync<int, int>("test.run", value, context))));
    Assert(factoryCalls == count && entered == count && disposed == count && results.Select(x => x.Value).Order().SequenceEqual(Enumerable.Range(0,count)));
});
await Check("independent-callers-share-one-business-contract-and-handler", async () =>
{
    var dispatcher = Dispatcher(() => new Handler((request, _, _) => ValueTask.FromResult(OperationResult<int>.Success(request * 3))));
    async Task<int> CallerOne(int value) => (await dispatcher.DispatchAsync<int, int>("test.run", value, context)).Value;
    async Task<int> CallerTwo(int value) => (await dispatcher.DispatchAsync<int, int>("test.run", value, context)).Value;
    Assert(await CallerOne(4) == 12 && await CallerTwo(4) == 12);
});
await Check("core-assembly-has-only-bcl-references", () => Sync(() =>
{
    var names = typeof(OperationDispatcher).Assembly.GetReferencedAssemblies().Select(x => x.Name!).ToArray();
    Assert(names.All(x => x.StartsWith("System.", StringComparison.Ordinal) || x == "netstandard"), string.Join(",", names));
}));

var shortDeadline = TimeSpan.FromMilliseconds(50);
var longDeadline = TimeSpan.FromHours(1);
var deadlineWatchdog = TimeSpan.FromSeconds(10); // Only a hang guard, not a timing assertion.

void AssertDeadlineFailure(OperationResult<int> result)
{
    Assert(OperationDeadline.DeadlineExceededCode == "operation.deadline_exceeded");
    Assert(!result.IsSuccess && result.Error.Code == OperationDeadline.DeadlineExceededCode &&
        result.Error.Message == "The operation deadline expired.");
}

await Check("operation-deadline-validates-timeout-bounds", () => Sync(() =>
{
    foreach (var timeout in new[] { TimeSpan.Zero, TimeSpan.FromTicks(-1), longDeadline.Add(TimeSpan.FromTicks(1)) })
        Assert(Throws<ArgumentOutOfRangeException>(() => new OperationDeadline(timeout)).ParamName == "timeout");
    Assert(new OperationDeadline(longDeadline).Timeout == longDeadline);
    Assert(new OperationDeadline(TimeSpan.FromTicks(1)).Timeout == TimeSpan.FromTicks(1));
}));
await Check("operation-deadline-rejects-null-operation", async () =>
{
    var error = await ThrowsAsync<ArgumentNullException>(() => new OperationDeadline(longDeadline).ExecuteAsync<int>(null!).AsTask());
    Assert(error.ParamName == "operation");
});
await Check("operation-deadline-pre-canceled-caller-skips-operation", async () =>
{
    using var caller = new CancellationTokenSource();
    caller.Cancel();
    var clock = new DeadlineTestClock();
    var calls = 0;
    var error = await ThrowsAsync<OperationCanceledException>(() => new OperationDeadline(longDeadline, clock)
        .ExecuteAsync(_ => { calls++; return ValueTask.FromResult(OperationResult<int>.Success(1)); }, caller.Token).AsTask());
    Assert(error.CancellationToken == caller.Token && calls == 0 && clock.Timer is null);
});
await Check("operation-deadline-preserves-immediate-success-and-business-failure", async () =>
{
    var deadline = new OperationDeadline(longDeadline);
    var calls = 0;
    foreach (var result in new[] { OperationResult<int>.Success(7), OperationResult<int>.Failure(new("business.denied", "Denied.")) })
    {
        var actual = await deadline.ExecuteAsync(token =>
        {
            calls++;
            Assert(token.CanBeCanceled && !token.IsCancellationRequested);
            return ValueTask.FromResult(result);
        });
        Assert(ReferenceEquals(actual, result));
    }
    Assert(calls == 2);
});
await Check("operation-deadline-default-clock-expiry-returns-exact-failure", async () =>
{
    var calls = 0;
    var result = await new OperationDeadline(shortDeadline, timeProvider: null).ExecuteAsync<int>(async token =>
    {
        calls++;
        await Task.Delay(Timeout.InfiniteTimeSpan, token);
        return OperationResult<int>.Success(1);
    }).AsTask().WaitAsync(deadlineWatchdog);
    AssertDeadlineFailure(result);
    Assert(calls == 1);
});
await Check("operation-deadline-does-not-abandon-work-that-ignores-cancellation", async () =>
{
    var clock = new DeadlineTestClock();
    var entered = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
    var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var expected = OperationResult<int>.Success(7);
    var pending = new OperationDeadline(shortDeadline, clock).ExecuteAsync(async token =>
    {
        entered.SetResult(token);
        await release.Task;
        return expected;
    }).AsTask();
    try
    {
        var token = await entered.Task.WaitAsync(deadlineWatchdog);
        Assert(clock.Timer?.DueTime == shortDeadline && !token.IsCancellationRequested);
        clock.Expire();
        Assert(token.IsCancellationRequested && !pending.IsCompleted);
    }
    finally { release.TrySetResult(); }
    Assert(ReferenceEquals(await pending.WaitAsync(deadlineWatchdog), expected) && clock.Timer?.Disposed == true);
});
await Check("operation-deadline-caller-cancellation-reaches-linked-token", async () =>
{
    using var caller = new CancellationTokenSource();
    var clock = new DeadlineTestClock();
    var error = await ThrowsAsync<OperationCanceledException>(() => new OperationDeadline(longDeadline, clock)
        .ExecuteAsync<int>(async token =>
        {
            Assert(token != caller.Token && !token.IsCancellationRequested);
            caller.Cancel();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return OperationResult<int>.Success(1);
        }, caller.Token).AsTask().WaitAsync(deadlineWatchdog));
    Assert(error.CancellationToken == caller.Token && clock.Timer?.Disposed == true);
});
await Check("operation-deadline-caller-cancellation-wins-after-expiry", async () =>
{
    using var caller = new CancellationTokenSource();
    var clock = new DeadlineTestClock();
    var error = await ThrowsAsync<OperationCanceledException>(() => new OperationDeadline(shortDeadline, clock)
        .ExecuteAsync<int>(async token =>
        {
            clock.Expire();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            catch (OperationCanceledException) { caller.Cancel(); throw; }
            return OperationResult<int>.Success(1);
        }, caller.Token).AsTask().WaitAsync(deadlineWatchdog));
    Assert(error.CancellationToken == caller.Token);
});
await Check("operation-deadline-preserves-foreign-cancellation-after-expiry", async () =>
{
    using var foreign = new CancellationTokenSource();
    foreign.Cancel();
    var expected = new OperationCanceledException(foreign.Token);
    var clock = new DeadlineTestClock();
    var actual = await ThrowsAsync<OperationCanceledException>(() => new OperationDeadline(shortDeadline, clock)
        .ExecuteAsync<int>(async token =>
        {
            clock.Expire();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            catch (OperationCanceledException) { throw expected; }
            return OperationResult<int>.Success(1);
        }).AsTask().WaitAsync(deadlineWatchdog));
    Assert(ReferenceEquals(actual, expected));
});
await Check("operation-deadline-does-not-convert-token-match-before-expiry", async () =>
{
    OperationCanceledException? expected = null;
    var clock = new DeadlineTestClock();
    var actual = await ThrowsAsync<OperationCanceledException>(() => new OperationDeadline(longDeadline, clock)
        .ExecuteAsync<int>(token =>
        {
            expected = new OperationCanceledException(token);
            throw expected;
        }).AsTask());
    Assert(ReferenceEquals(actual, expected) && clock.Timer?.Disposed == true);
});
await Check("operation-deadline-preserves-dispatch-handler-fault", async () =>
{
    var expected = new ApplicationException("handler fault");
    var handler = new Handler((_, _, _) => throw expected);
    var clock = new DeadlineTestClock();
    var actual = await ThrowsAsync<ApplicationException>(() => new OperationDeadline(longDeadline, clock)
        .DispatchAsync<int, int>(Dispatcher(() => handler), "test.run", 1, context).AsTask());
    Assert(ReferenceEquals(actual, expected) && handler.DisposeCalls == 1 && clock.Timer?.Disposed == true);
});
await Check("operation-deadline-rejects-null-result", async () =>
{
    var clock = new DeadlineTestClock();
    await ThrowsAsync<InvalidOperationException>(() => new OperationDeadline(longDeadline, clock)
        .ExecuteAsync(_ => ValueTask.FromResult<OperationResult<int>>(null!)).AsTask());
    Assert(clock.Timer?.Disposed == true);
});
await Check("operation-deadline-dispatch-validates-arguments-before-work", async () =>
{
    var clock = new DeadlineTestClock();
    var deadline = new OperationDeadline(longDeadline, clock);
    var calls = 0;
    var dispatcher = Dispatcher(() => { calls++; return new Handler(); });
    using var canceled = new CancellationTokenSource();
    canceled.Cancel();
    Assert((await ThrowsAsync<ArgumentNullException>(() => deadline
        .DispatchAsync<int, int>(null!, "Bad", 1, null!, canceled.Token).AsTask())).ParamName == "dispatcher");
    Assert((await ThrowsAsync<ArgumentException>(() => deadline
        .DispatchAsync<int, int>(dispatcher, "Bad", 1, null!, canceled.Token).AsTask())).ParamName == "value");
    Assert((await ThrowsAsync<ArgumentNullException>(() => deadline
        .DispatchAsync<int, int>(dispatcher, "test.run", 1, null!, canceled.Token).AsTask())).ParamName == "context");
    Assert((await ThrowsAsync<OperationCanceledException>(() => deadline
        .DispatchAsync<int, int>(dispatcher, "test.run", 1, context, canceled.Token).AsTask())).CancellationToken == canceled.Token);
    Assert(calls == 0 && clock.Timer is null);
});
await Check("operation-deadline-dispatch-runs-registered-handler-and-disposes-timer", async () =>
{
    var clock = new DeadlineTestClock();
    var handler = new Handler((request, suppliedContext, token) =>
    {
        Assert(ReferenceEquals(suppliedContext, context) && !token.IsCancellationRequested);
        return ValueTask.FromResult(OperationResult<int>.Success(request * 2));
    });
    var result = await new OperationDeadline(longDeadline, clock)
        .DispatchAsync<int, int>(Dispatcher(() => handler), "test.run", 6, context);
    Assert(result.Value == 12 && handler.DisposeCalls == 1 && clock.Timer?.Disposed == true);
});

foreach (var useBudget in new[] { false, true })
    await Check(useBudget ? "operation-deadline-expiry-releases-concurrency-permit" :
        "operation-deadline-dispatch-expiry-disposes-handler-and-observes-canceled", async () =>
    {
        var clock = new DeadlineTestClock();
        var deadline = new OperationDeadline(shortDeadline, clock);
        var budget = new OperationConcurrencyBudget(1);
        var observed = new List<OperationDispatchOutcome>();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new Handler(async (_, _, token) =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return OperationResult<int>.Success(1);
        });
        var dispatcher = Dispatcher(() => handler, completion => observed.Add(completion.Outcome));
        var pending = (useBudget
            ? deadline.ExecuteAsync(token => budget.DispatchAsync<int, int>(dispatcher, "test.run", 1, context, token))
            : deadline.DispatchAsync<int, int>(dispatcher, "test.run", 1, context)).AsTask();
        await entered.Task.WaitAsync(deadlineWatchdog);
        try { Assert(!useBudget || budget.GetSnapshot().ActiveOperations == 1); }
        finally { clock.Expire(); }
        AssertDeadlineFailure(await pending.WaitAsync(deadlineWatchdog));
        Assert(handler.DisposeCalls == 1 && observed.SequenceEqual([OperationDispatchOutcome.Canceled]));
        Assert(budget.GetSnapshot().ActiveOperations == 0);
    });

var output = new
{
    observedAtUtc = DateTimeOffset.UtcNow,
    framework = RuntimeInformation.FrameworkDescription,
    os = RuntimeInformation.OSDescription,
    architecture = RuntimeInformation.ProcessArchitecture.ToString(),
    scope = "BCL-only experimental Core component contracts. Not tenant/identity authority, supported-Windows/hosted-CI/transport/provider/E2E/benchmark/production qualification.",
    passed = checks.Count - failures,
    failed = failures,
    tests = checks
};
var json = JsonSerializer.Serialize(output, new JsonSerializerOptions { WriteIndented = true });
var outputArgument = args.SingleOrDefault(x => x.StartsWith("--output=", StringComparison.Ordinal));
if (outputArgument is not null)
{
    var path = Path.GetFullPath(outputArgument[9..]);
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    await File.WriteAllTextAsync(path, json + "\n");
}
Console.WriteLine(json);
return failures == 0 ? 0 : 1;

sealed class Handler(
    Func<int, OperationContext, CancellationToken, ValueTask<OperationResult<int>>>? action = null,
    Action? cleanup = null) : IOperationHandler<int, int>, IDisposable
{
    public int DisposeCalls { get; private set; }
    public ValueTask<OperationResult<int>> HandleAsync(int request, OperationContext context, CancellationToken cancellationToken)
        => action?.Invoke(request, context, cancellationToken) ?? ValueTask.FromResult(OperationResult<int>.Success(request));
    public void Dispose() { DisposeCalls++; cleanup?.Invoke(); }
}

sealed class AsyncHandler(Func<ValueTask> cleanup) : IOperationHandler<int, int>, IAsyncDisposable, IDisposable
{
    public int AsyncDisposeCalls { get; private set; }
    public int SyncDisposeCalls { get; private set; }
    public ValueTask<OperationResult<int>> HandleAsync(int request, OperationContext context, CancellationToken cancellationToken)
        => ValueTask.FromResult(OperationResult<int>.Success(request));
    public async ValueTask DisposeAsync() { AsyncDisposeCalls++; await cleanup(); }
    public void Dispose() => SyncDisposeCalls++;
}

sealed class DeadlineTestClock : TimeProvider
{
    public ProbeTimer? Timer { get; private set; }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        => Timer = new ProbeTimer(callback, state, dueTime);

    public void Expire() => (Timer ?? throw new InvalidOperationException("No deadline timer was created.")).Fire();

    public sealed class ProbeTimer(TimerCallback callback, object? state, TimeSpan dueTime) : ITimer
    {
        public TimeSpan DueTime { get; } = dueTime;
        public bool Disposed { get; private set; }
        public void Fire() { if (!Disposed) callback(state); }
        public bool Change(TimeSpan dueTime, TimeSpan period) => throw new NotSupportedException();
        public void Dispose() => Disposed = true;
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
