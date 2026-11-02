using SunatRAG.Core.Retrieval;

namespace SunatRAG.Core.Evaluation;

/// <summary>One golden case run through the retriever, with its relevance judgement.</summary>
/// <param name="FirstEvidenceRank">1-based position (in the list that reaches the prompt) of the first answer-bearing chunk; null = none.</param>
/// <param name="DocumentHit">Any chunk in the prompt belongs to an expected document.</param>
public sealed record CaseRetrieval(
    GoldenCase Case,
    string Query,
    RetrievalResult Result,
    int? FirstEvidenceRank,
    bool DocumentHit)
{
    public bool EvidenceHit => FirstEvidenceRank is not null;
}

/// <param name="EvidenceHit">Share of cases where an answer-bearing chunk reached the prompt. The headline number.</param>
/// <param name="DocHit">Same at document level (always >= EvidenceHit: the right document, maybe the wrong part of it).</param>
/// <param name="Mrr">Mean reciprocal rank of the first answer-bearing chunk (0 when missing).</param>
public sealed record RetrievalSummary(
    string Label,
    int Cases,
    double EvidenceHit,
    double DocHit,
    double Mrr,
    double MeanContextTokens,
    double MeanBestDistance,
    TimeSpan MeanRetrievalTime);

public static class RetrievalMetrics
{
    public static CaseRetrieval Score(GoldenCase golden, string query, RetrievalResult result)
    {
        int? first = null;
        for (var i = 0; i < result.Chunks.Count; i++)
        {
            if (!golden.IsEvidence(result.Chunks[i].Record.Text)) continue;
            first = i + 1;
            break;
        }

        var documentHit = result.Chunks.Any(c => golden.ExpectedDocuments.Contains(c.Record.DocumentId, StringComparer.Ordinal));
        return new CaseRetrieval(golden, query, result, first, documentHit);
    }

    public static RetrievalSummary Summarize(string label, IReadOnlyList<CaseRetrieval> runs)
    {
        if (runs.Count == 0) return new RetrievalSummary(label, 0, 0, 0, 0, 0, double.NaN, TimeSpan.Zero);

        var distances = runs.Select(r => r.Result.Diagnostics.BestVectorDistance).Where(d => d is not null).Select(d => d!.Value).ToList();
        var times = runs.Select(r =>
        {
            var d = r.Result.Diagnostics;
            // Embed and vector search are sequential; BM25 runs concurrently with both, so wall time is max(dense path, keyword).
            return TimeSpan.FromTicks(Math.Max((d.Embed + d.Vector).Ticks, d.Keyword.Ticks) + d.Rerank.Ticks);
        }).ToList();

        return new RetrievalSummary(
            label,
            runs.Count,
            runs.Count(r => r.EvidenceHit) / (double)runs.Count,
            runs.Count(r => r.DocumentHit) / (double)runs.Count,
            runs.Average(r => r.FirstEvidenceRank is { } rank ? 1.0 / rank : 0.0),
            runs.Average(r => r.Result.Diagnostics.UsedTokens),
            distances.Count == 0 ? double.NaN : distances.Average(),
            TimeSpan.FromTicks((long)times.Average(t => t.Ticks)));
    }
}
