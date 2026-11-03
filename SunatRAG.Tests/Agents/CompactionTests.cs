using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using SunatRAG.Core.Agents;
using SunatRAG.Core.Configuration;
using SunatRAG.Core.Retrieval;
using static SunatRAG.Tests.Agents.AgentTestSupport;

namespace SunatRAG.Tests.Agents;

public class CompactionThresholdsTests
{
    private const int Context = 16384; // input budget = 16384 - 4096 = 12288

    [Fact]
    public void ContextProvider_keeps_the_history_inside_its_reservation_and_has_nothing_to_evict()
    {
        var t = CompactionThresholds.Derive(new AgentOptions { Retrieval = RetrievalStrategy.ContextProvider }, new RetrievalOptions(), Context);

        // 2000 real tokens of history x 0.89 (estimator / real) / 12288 = 0.1449
        Assert.Equal(0.1449, t.TruncateAt, precision: 3);
        Assert.Equal(t.EvictAt, t.TruncateAt); // no tool results in the history: truncation does the work
    }

    [Fact]
    public void Tool_strategy_follows_the_frameworks_50_and_80_percent_minus_the_overhead_the_estimator_cannot_see()
    {
        var t = CompactionThresholds.Derive(new AgentOptions { Retrieval = RetrievalStrategy.Tool }, new RetrievalOptions(), Context);

        Assert.Equal((0.5 * 12288 - 1500) * 0.89 / 12288, t.EvictAt, precision: 4);   // ~0.336, not 0.5
        Assert.Equal((0.8 * 12288 - 1500) * 0.89 / 12288, t.TruncateAt, precision: 4); // ~0.603, not 0.8
    }

    [Fact]
    public void Explicit_values_win_but_only_when_both_are_given()
    {
        var both = CompactionThresholds.Derive(
            new AgentOptions { CompactionEvictAt = 0.3, CompactionTruncateAt = 0.7 }, new RetrievalOptions(), Context);
        var half = CompactionThresholds.Derive(
            new AgentOptions { CompactionEvictAt = 0.3 }, new RetrievalOptions(), Context);

        Assert.Equal(new CompactionThresholds(0.3, 0.7), both);
        Assert.NotEqual(0.3, half.EvictAt); // half an override is ignored instead of producing a confusing mix
    }

    [Fact]
    public void Truncation_is_never_below_eviction_even_with_inconsistent_overrides()
    {
        var t = CompactionThresholds.Derive(
            new AgentOptions { CompactionEvictAt = 0.6, CompactionTruncateAt = 0.4 }, new RetrievalOptions(), Context);

        Assert.True(t.TruncateAt >= t.EvictAt); // the framework constructor rejects the opposite
    }

    [Fact]
    public void A_tiny_reservation_is_clamped_to_a_sane_floor()
    {
        var t = CompactionThresholds.Derive(
            new AgentOptions { Retrieval = RetrievalStrategy.ContextProvider }, new RetrievalOptions { ReservedHistoryTokens = 1 }, Context);

        Assert.Equal(0.05, t.TruncateAt);
    }
}

/// <summary>
/// The derived thresholds against the REAL compaction strategy: old turns must leave the prompt once the history outgrows its
/// reservation. This is the behaviour the Phase 5 live run could not explain (a 6.3k-token prompt that never compacted).
/// </summary>
public class CompactionBehaviourTests
{
    private readonly IChunkRetriever _retriever = Substitute.For<IChunkRetriever>();

    public CompactionBehaviourTests()
    {
        var chunk = Chunk(Record("r#0004", "superin-2025-000386-2025", "Artículo 6. La declaración se presenta hasta el 31 de marzo."));
        _retriever.RetrieveAsync(Arg.Any<RetrievalRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result("q", 0.31, chunk)));
    }

    // ~2400 characters = ~600 tokens for the framework's bytes/4 estimator = ~670 real qwen3 tokens: one such answer per turn.
    private static readonly string LongAnswer = string.Join(' ', Enumerable.Repeat("palabra", 300));

    private static ScriptedChatClient FiveTurnModel() =>
        new(Enumerable.Range(0, 5).Select(_ => (IList<AIContent>)[new TextContent(LongAnswer)]).ToArray());

    private SunatChatSession CreateSession(ScriptedChatClient model, bool compact)
    {
        var chat = new ChatClientBuilder(model).UseFunctionInvocation().Build();
        var options = new AgentOptions { Retrieval = RetrievalStrategy.ContextProvider, EnableSkills = false, CompactHistory = compact };
        var factory = new SunatAgentFactory(
            chat, _retriever, Options.Create(options), Options.Create(new RetrievalOptions()), Options.Create(new AiOptions()), NullLoggerFactory.Instance);
        return new SunatChatSession(factory);
    }

    private static async Task AskAllAsync(SunatChatSession session, int turns)
    {
        for (var i = 1; i <= turns; i++)
            await foreach (var _ in session.AskAsync($"pregunta-{i}")) { }
    }

    private static bool Mentions(ScriptedChatClient.Call call, string text) =>
        call.Messages.Any(m => ScriptedChatClient.Call.Flatten(m).Contains(text, StringComparison.Ordinal));

    [Fact]
    public async Task Without_compaction_the_first_question_is_still_in_the_fifth_prompt()
    {
        var model = FiveTurnModel();
        using var session = CreateSession(model, compact: false);

        await AskAllAsync(session, 5);

        Assert.True(Mentions(model.Calls[4], "pregunta-1")); // control: history just keeps growing
    }

    [Fact]
    public async Task With_the_derived_thresholds_old_turns_leave_the_prompt_and_the_recent_ones_stay()
    {
        var model = FiveTurnModel();
        using var session = CreateSession(model, compact: true);

        await AskAllAsync(session, 5);

        Assert.True(Mentions(model.Calls[1], "pregunta-1"));  // still fits in the reservation after one turn
        Assert.False(Mentions(model.Calls[4], "pregunta-1")); // dropped by the fifth call
        Assert.True(Mentions(model.Calls[4], "pregunta-4"));  // the recent context survives
        Assert.True(Mentions(model.Calls[4], "pregunta-5"));  // and so does the question being asked now
    }
}
