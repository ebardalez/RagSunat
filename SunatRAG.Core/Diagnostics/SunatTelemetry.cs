using System.Diagnostics;

namespace SunatRAG.Core.Diagnostics;

/// <summary>
/// Our own spans. MEAI already emits spans for model calls and tool executions (source "SunatRAG", set in AddSunatAi), and the agent
/// wrapper emits "invoke_agent"; what was missing is retrieval, which is where the non-model time goes. One ActivitySource per
/// subsystem; the OpenTelemetry SDK (or the LatencyTracer below) subscribes by NAME, so instrumentation costs nothing when nobody listens:
/// StartActivity returns null and every `activity?.SetTag` is a no-op.
/// </summary>
public static class SunatTelemetry
{
    public const string RetrievalSourceName = "SunatRAG.Retrieval";

    public static readonly ActivitySource Retrieval = new(RetrievalSourceName);
}
