using SunatRAG.Core.Configuration;

namespace SunatRAG.Core.Agents;

/// <summary>
/// The two fractions ContextWindowCompactionStrategy needs, derived from the context budget instead of guessed.
///
/// WHY the framework default (evict at 0.5, truncate at 0.8 of the input budget) did nothing here. The strategy compares its OWN token
/// estimate of the MESSAGE LIST against fractions of (context window − max output). Three things make that estimate differ from what
/// Ollama really receives (measured 2026-11-03 against qwen3:8b, see HANDOFF "Calibration"):
///   1. The estimator is UTF-8 bytes / 4. For this Spanish legal text qwen3 needs ~3.5 characters per token, so the estimate is ~0.89 of the
///      real count (<see cref="AgentOptions.CompactionEstimatorRatio"/>).
///   2. It sees messages only. The system prompt, the skills advertisement and the tool schemas travel in ChatOptions, not in messages
///      (~<see cref="RetrievalOptions.ReservedSystemTokens"/> real tokens it never counts).
///   3. With the ContextProvider strategy the retrieved sources are injected as transient INSTRUCTIONS for each call, so they are invisible
///      to it too. What it sees there is just the conversation history, and the prompt budget (<see cref="RetrievalOptions"/>) reserves
///      <see cref="RetrievalOptions.ReservedHistoryTokens"/> real tokens for exactly that.
///
/// So each fraction answers "at what estimator reading is the REAL prompt at the level I care about?":
///   ContextProvider: history alone must stay within ReservedHistoryTokens (else the chunk budget, computed assuming that reserve, overflows
///                    the window and Ollama silently truncates the prompt). Nothing to "evict" (there are no tool results), so both
///                    thresholds are the same and truncation does the work.
///   Tool:            messages include the tool results (the sources), so the thresholds are the framework's intent (evict when the REAL prompt
///                    reaches 50 % of the input budget, truncate at 80 %) minus the fixed overhead the estimator cannot see.
/// </summary>
public readonly record struct CompactionThresholds(double EvictAt, double TruncateAt)
{
    private const double Floor = 0.05;

    public static CompactionThresholds Derive(AgentOptions agent, RetrievalOptions retrieval, int contextLength)
    {
        // Explicit configuration always wins, but both must be given: half an override would produce a confusing mix.
        if (agent.CompactionEvictAt is { } evict && agent.CompactionTruncateAt is { } truncate)
            return new CompactionThresholds(evict, Math.Max(evict, truncate));

        var inputBudget = Math.Max(1, contextLength - retrieval.ReservedAnswerTokens);

        // Real tokens of MESSAGES -> fraction of the input budget in the estimator's units.
        double Fraction(double realMessageTokens) =>
            Math.Clamp(realMessageTokens * agent.CompactionEstimatorRatio / inputBudget, Floor, 1.0);

        if (agent.Retrieval == RetrievalStrategy.ContextProvider)
        {
            var history = Fraction(retrieval.ReservedHistoryTokens);
            return new CompactionThresholds(history, history);
        }

        var evictAt = Fraction(0.5 * inputBudget - retrieval.ReservedSystemTokens);
        var truncateAt = Fraction(0.8 * inputBudget - retrieval.ReservedSystemTokens);
        return new CompactionThresholds(evictAt, Math.Max(evictAt, truncateAt));
    }
}
