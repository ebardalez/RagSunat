namespace SunatRAG.Core.Configuration;

/// <summary>Settings for hybrid retrieval (section "Retrieval").</summary>
public sealed class RetrievalOptions
{
    public const string SectionName = "Retrieval";

    // ── Candidate generation ─────────────────────────────────────────────────────────────

    /// <summary>How many candidates each ranker (dense vectors, BM25) contributes to the fusion.</summary>
    public int CandidatesPerRanker { get; init; } = 30;

    /// <summary>
    /// The k in Reciprocal Rank Fusion: score = sum over rankers of weight / (k + rank).
    /// 60 is the constant from the original paper (Cormack et al., SIGIR 2009) and the default in most search engines.
    /// Smaller k = the top of each list matters more; larger k = flatter, more democratic.
    /// </summary>
    public int RrfK { get; init; } = 60;

    public double VectorWeight { get; init; } = 1.0;
    public double KeywordWeight { get; init; } = 1.0;

    /// <summary>
    /// Filters that cannot be pushed into the stores (e.g. "not a draft") are applied after the search, so each ranker
    /// fetches CandidatesPerRanker * FilterOverfetch rows and keeps the first CandidatesPerRanker that survive.
    /// </summary>
    public int FilterOverfetch { get; init; } = 3;

    // ── What goes into the prompt ────────────────────────────────────────────────────────

    /// <summary>Max chunks handed to the LLM.</summary>
    public int TopK { get; init; } = 6;

    /// <summary>
    /// Diversity cap. One long resolución can produce 20 near-identical chunks; without a cap they crowd out every other source.
    /// </summary>
    public int MaxChunksPerDocument { get; init; } = 3;

    // ── Context-window budget (Ai:Ollama:ContextLength is the total) ─────────────────────

    /// <summary>System prompt + skill instructions + tool schemas (Phase 5 will measure the real number).</summary>
    public int ReservedSystemTokens { get; init; } = 1500;

    /// <summary>Previous turns of the conversation.</summary>
    public int ReservedHistoryTokens { get; init; } = 2000;

    /// <summary>
    /// Room for the answer. qwen3 is a "thinking" model: its reasoning tokens count against the same window,
    /// so this must cover thinking + answer, not just the answer.
    /// </summary>
    public int ReservedAnswerTokens { get; init; } = 4096;

    /// <summary>
    /// Our chunk token counts come from the bge-m3 tokenizer; the chat model (qwen3) tokenizes Spanish differently, usually into
    /// more tokens. Multiply before budgeting. Calibrate against Ollama's prompt_eval_count (see the handoff).
    /// </summary>
    public double ChatTokenSafetyFactor { get; init; } = 1.25;

    /// <summary>"[3] Resolución ... pág. 2 (Artículo 4)" header + separators around each chunk.</summary>
    public int PerChunkOverheadTokens { get; init; } = 40;

    // ── LLM rerank (experiment) ──────────────────────────────────────────────────────────

    /// <summary>How many fused candidates are shown to the model for reordering.</summary>
    public int RerankCandidates { get; init; } = 10;

    /// <summary>Each candidate is truncated to this many characters in the rerank prompt (about 150-200 tokens).</summary>
    public int RerankCharsPerChunk { get; init; } = 600;
}
