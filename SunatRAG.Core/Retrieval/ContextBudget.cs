using SunatRAG.Core.Configuration;

namespace SunatRAG.Core.Retrieval;

/// <summary>
/// The token arithmetic of a RAG prompt. Why it is correctness, not tuning: when prompt + answer exceed the server's num_ctx,
/// Ollama does not fail; it silently truncates the prompt (and logs a warning only on the server). The model then answers
/// from a prompt that lost its beginning, usually the instructions, and nothing on the client side tells you.
///
///   ContextLength (Ai:Ollama:ContextLength, e.g. 16384; must match OLLAMA_CONTEXT_LENGTH / the Modelfile num_ctx)
///   − system prompt + skills + tool schemas   (ReservedSystemTokens)
///   − conversation history                    (ReservedHistoryTokens)
///   − thinking + answer                       (ReservedAnswerTokens)
///   − the question itself
///   = tokens available for retrieved chunks
/// </summary>
public static class ContextBudget
{
    /// <summary>Spanish averages roughly 3-4 characters per token; 3 errs on the safe (larger) side.</summary>
    public static int EstimateQuestionTokens(string question) => (int)Math.Ceiling(question.Length / 3.0);

    public static int Available(int contextLength, RetrievalOptions o, int questionTokens) =>
        Math.Max(0, contextLength - o.ReservedSystemTokens - o.ReservedHistoryTokens - o.ReservedAnswerTokens - questionTokens);

    /// <summary>Cost of one chunk in CHAT-model tokens: stored (bge-m3) count × safety factor + header overhead.</summary>
    public static int ChunkCost(int storedTokenCount, RetrievalOptions o) =>
        (int)Math.Ceiling(storedTokenCount * o.ChatTokenSafetyFactor) + o.PerChunkOverheadTokens;
}

public sealed record ContextSelection<T>(
    IReadOnlyList<T> Selected, int UsedTokens, int SkippedForDocumentCap, int SkippedForBudget);

public static class ContextSelector
{
    /// <summary>
    /// Walks the ranked list once, in order, and keeps an item unless (a) its document already has maxPerDocument items, or
    /// (b) it does not fit in what is left of the budget. A skipped item does not stop the walk: a shorter, lower-ranked chunk
    /// may still fit. Stops at topK items.
    /// </summary>
    public static ContextSelection<T> Select<T>(
        IEnumerable<T> ranked, int topK, int maxPerDocument, int budgetTokens, Func<T, string> documentOf, Func<T, int> costOf)
    {
        var selected = new List<T>();
        var perDocument = new Dictionary<string, int>(StringComparer.Ordinal);
        int used = 0, skippedDoc = 0, skippedBudget = 0;

        foreach (var item in ranked)
        {
            if (selected.Count >= topK) break;

            var doc = documentOf(item);
            perDocument.TryGetValue(doc, out var already);
            if (already >= maxPerDocument) { skippedDoc++; continue; }

            var cost = costOf(item);
            if (used + cost > budgetTokens) { skippedBudget++; continue; }

            selected.Add(item);
            used += cost;
            perDocument[doc] = already + 1;
        }

        return new ContextSelection<T>(selected, used, skippedDoc, skippedBudget);
    }
}
