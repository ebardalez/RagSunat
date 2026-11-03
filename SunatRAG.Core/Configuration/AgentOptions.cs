using Microsoft.Extensions.AI;

namespace SunatRAG.Core.Configuration;

/// <summary>How the agent gets retrieved sources into the prompt.</summary>
public enum RetrievalStrategy
{
    /// <summary>
    /// Retrieval is an AIFunction (<c>buscar_normativa</c>). The model decides WHEN to search and rewrites the question into
    /// regulatory vocabulary first. Costs one extra model round-trip (tool call, then answer) and needs a model that calls tools reliably.
    /// </summary>
    Tool,

    /// <summary>
    /// An AIContextProvider searches with the user's raw question before every model call and injects the sources as transient
    /// instructions. One model call, works with models that are bad at tools, but no query rewriting and it also searches for "hola".
    /// </summary>
    ContextProvider
}

/// <summary>Settings for the Phase 5 agent (section "Agent").</summary>
public sealed class AgentOptions
{
    public const string SectionName = "Agent";

    public RetrievalStrategy Retrieval { get; init; } = RetrievalStrategy.ContextProvider; // measured in Phase 6: 93% vs 50% on the golden set

    /// <summary>Folder that contains one sub-folder per skill (<c>sunat-normas/SKILL.md</c>). Relative paths resolve against AppContext.BaseDirectory.</summary>
    public string SkillsPath { get; init; } = "skills";

    public bool EnableSkills { get; init; } = true;

    /// <summary>
    /// Provider-neutral "how much should the model think". qwen3 is a thinking model: with a tool loop (two or three model calls per
    /// question) leaving it on multiplies latency on this hardware, so the default is None. Try Low/Medium to measure the quality gain.
    /// </summary>
    public ReasoningEffort Reasoning { get; init; } = ReasoningEffort.None;

    public float Temperature { get; init; } = 0.2f;

    /// <summary>
    /// Evict old tool results / old turns so the conversation keeps fitting in Ai:Ollama:ContextLength. Without it the second question
    /// would carry the first question's ~8k tokens of retrieved chunks and Ollama would silently truncate the prompt.
    /// </summary>
    public bool CompactHistory { get; init; } = true;

    /// <summary>
    /// Explicit override of the fraction of the input budget (context window - max output) at which old tool results are collapsed.
    /// Leave null: <see cref="SunatRAG.Core.Agents.CompactionThresholds"/> derives it from the context budget (the framework's own default,
    /// 0.5, never fired here: see that class for why).
    /// </summary>
    public double? CompactionEvictAt { get; init; }

    /// <summary>Explicit override for dropping the oldest turns. Null = derived.</summary>
    public double? CompactionTruncateAt { get; init; }

    /// <summary>
    /// The compaction framework counts tokens as UTF-8 bytes / 4 unless given a tokenizer. MEASURED against Ollama (qwen3:8b, 25 chunks of the
    /// real corpus, 2026-11-03): estimator / real tokens = 0.89 for Spanish legal text. Used to convert "real tokens" into the estimator's units.
    /// </summary>
    public double CompactionEstimatorRatio { get; init; } = 0.89;

    /// <summary>
    /// Best cosine distance above which the tool warns the model that the match is weak. UNCALIBRATED: Phase 4 measured 0.31 for a
    /// well-phrased query and 0.43-0.44 for a colloquial one that missed. Phase 6 must pick it from the golden set.
    /// </summary>
    public double WeakMatchDistance { get; init; } = 0.40;
}
