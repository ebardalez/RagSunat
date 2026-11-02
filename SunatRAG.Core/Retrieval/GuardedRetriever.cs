using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SunatRAG.Core.Configuration;

namespace SunatRAG.Core.Retrieval;

/// <summary>
/// The "weak match" guard, as CODE.
///
/// Phase 5 measured the failure this fixes: with a colloquial question the retriever's best distance was 0.405, the tool result carried
/// an "AVISO: coincidencia débil" paragraph, and the 8B model ignored it and answered from an unrelated chunk. A warning in the prompt is a
/// request; the model may or may not honour it. Removing the chunks is a guarantee: if there is nothing in the context, the model has
/// nothing to misquote, and the empty-result message already tells it to say so.
///
/// It is a DECORATOR over <see cref="IChunkRetriever"/>: same interface in and out, so the agent layer neither knows nor cares, and the
/// eval can run the unguarded retriever to measure distances. It is the retrieval-side sibling of IChatClient middleware.
/// </summary>
public sealed class GuardedRetriever(
    IChunkRetriever inner,
    IOptions<GuardOptions> options,
    ILogger<GuardedRetriever> logger) : IChunkRetriever
{
    public async Task<RetrievalResult> RetrieveAsync(RetrievalRequest request, CancellationToken ct = default)
    {
        var result = await inner.RetrieveAsync(request, ct);
        var guard = options.Value;
        if (!guard.Enabled || result.Chunks.Count == 0) return result;

        var best = result.Diagnostics.BestVectorDistance;
        if (best is not { } distance || distance <= guard.MaxBestDistance) return result;

        logger.LogInformation(
            "Guard: \"{Query}\" descartada, mejor distancia {Best:0.000} > {Max:0.000}; se entregan 0 de {Count} fragmentos.",
            request.Query, distance, guard.MaxBestDistance, result.Chunks.Count);

        return result with { Chunks = [], Diagnostics = result.Diagnostics with { Refused = true } };
    }
}
