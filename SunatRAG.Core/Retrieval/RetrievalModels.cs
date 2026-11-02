using SunatRAG.Core.Indexing;

namespace SunatRAG.Core.Retrieval;

public enum RetrievalMode
{
    /// <summary>Dense + BM25, fused with RRF (the default).</summary>
    Hybrid,

    /// <summary>Dense only. Useful as a baseline to see what BM25 adds.</summary>
    VectorOnly,

    /// <summary>BM25 only. Useful as a baseline to see what the embeddings add.</summary>
    KeywordOnly
}

public enum RerankMode
{
    None,

    /// <summary>Ask the chat model to reorder the top fused candidates. An experiment: costs seconds on a local model.</summary>
    Llm
}

public sealed record RetrievalRequest(
    string Query,
    SearchFilters? Filters = null,
    int? TopK = null,
    RetrievalMode Mode = RetrievalMode.Hybrid,
    RerankMode Rerank = RerankMode.None);

/// <summary>
/// One chunk selected for the prompt, with enough provenance to explain the ranking and to render a citation.
/// The LLM cites by position ("[2]"); the app resolves the number back to Record (document, pages, URL), so a model can
/// never invent a URL or a page number.
/// </summary>
/// <param name="Score">RRF score (hybrid) or the single ranker's fused score.</param>
/// <param name="VectorDistance">Cosine distance, lower = closer. Null if the dense ranker did not return this chunk.</param>
/// <param name="Bm25Score">Negated FTS5 bm25, higher = better. Null if BM25 did not return this chunk.</param>
/// <param name="ContextTokens">What this chunk costs in the chat prompt (see <see cref="ContextBudget.ChunkCost"/>).</param>
public sealed record RetrievedChunk(
    ChunkRecord Record,
    double Score,
    int? VectorRank,
    int? KeywordRank,
    double? VectorDistance,
    double? Bm25Score,
    int ContextTokens)
{
    public string Cita => Record.Cita();
}

public sealed record RetrievalDiagnostics(
    RetrievalMode Mode,
    TimeSpan Embed,
    TimeSpan Vector,
    TimeSpan Keyword,
    TimeSpan Rerank,
    int VectorCandidates,
    int KeywordCandidates,
    int FusedCandidates,
    double? BestVectorDistance,
    int ContextLength,
    int BudgetTokens,
    int UsedTokens,
    int SkippedForDocumentCap,
    int SkippedForBudget,
    bool RerankApplied,
    bool RerankFellBack,
    bool Refused = false);

public sealed record RetrievalResult(string Query, IReadOnlyList<RetrievedChunk> Chunks, RetrievalDiagnostics Diagnostics);
