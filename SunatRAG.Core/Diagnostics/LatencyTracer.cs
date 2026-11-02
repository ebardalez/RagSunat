using System.Collections.Concurrent;
using System.Diagnostics;

namespace SunatRAG.Core.Diagnostics;

/// <param name="Wall">Wall-clock time of the whole question, measured by the caller.</param>
/// <param name="PromptTokensPerCall">Input tokens the server reported for each model call, in order (gen_ai.usage.input_tokens).</param>
public sealed record LatencyBreakdown(
    TimeSpan Wall,
    int ModelCalls,
    TimeSpan ModelTime,
    int ToolCalls,
    TimeSpan ToolTime,
    int Retrievals,
    TimeSpan RetrievalTime,
    IReadOnlyList<long> PromptTokensPerCall)
{
    /// <summary>Wall time not explained by a model call or a tool: agent plumbing, history compaction, UI/streaming overhead.</summary>
    public TimeSpan Other => Wall > ModelTime + ToolTime ? Wall - ModelTime - ToolTime : TimeSpan.Zero;
}

/// <summary>
/// Collects finished spans in-process using only System.Diagnostics (no exporter package). It is the same mechanism the OpenTelemetry SDK
/// is built on: an ActivityListener says which ActivitySources it wants, and receives every Activity when it stops.
///
/// Why not just read the OTel console exporter? Because the eval needs the numbers as data (to compute p50/p95 and put them in a table),
/// not as text scrolling by. The production path (OTel -> console/OTLP) and this one see the very same spans.
/// Use: tracer.Begin(); run one question; tracer.Collect(wall). One question at a time (spans are not tagged per case).
/// </summary>
public sealed class LatencyTracer : IDisposable
{
    private readonly ActivityListener _listener;
    private readonly ConcurrentQueue<Activity> _finished = new();

    public LatencyTracer(params string[] sourceNames)
    {
        var wanted = new HashSet<string>(sourceNames, StringComparer.Ordinal);
        _listener = new ActivityListener
        {
            ShouldListenTo = source => wanted.Contains(source.Name),
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            SampleUsingParentId = (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => _finished.Enqueue(activity)
        };
        ActivitySource.AddActivityListener(_listener);
    }

    /// <summary>Forget everything collected so far (call right before a question).</summary>
    public void Begin()
    {
        while (_finished.TryDequeue(out _)) { }
    }

    public LatencyBreakdown Collect(TimeSpan wall)
    {
        var spans = _finished.ToArray();
        // Operation names: MEAI uses "chat {model}" and "execute_tool {name}"; the agent wrapper "invoke_agent {name}"; we use "sunat.retrieve".
        var chat = spans.Where(a => a.OperationName.StartsWith("chat", StringComparison.Ordinal)).OrderBy(a => a.StartTimeUtc).ToList();
        var tools = spans.Where(a => a.OperationName.StartsWith("execute_tool", StringComparison.Ordinal)).ToList();
        var retrievals = spans.Where(a => a.OperationName == "sunat.retrieve").ToList();

        var prompts = chat
            .Select(a => a.GetTagItem("gen_ai.usage.input_tokens"))
            .Where(v => v is not null)
            .Select(v => Convert.ToInt64(v, System.Globalization.CultureInfo.InvariantCulture))
            .ToList();

        return new LatencyBreakdown(
            wall,
            chat.Count, Sum(chat),
            tools.Count, Sum(tools),
            retrievals.Count, Sum(retrievals),
            prompts);
    }

    private static TimeSpan Sum(IEnumerable<Activity> spans) => TimeSpan.FromTicks(spans.Sum(a => a.Duration.Ticks));

    public void Dispose() => _listener.Dispose();
}
