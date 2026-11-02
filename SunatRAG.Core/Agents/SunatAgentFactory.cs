using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Compaction;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SunatRAG.Core.Composition;
using SunatRAG.Core.Configuration;
using SunatRAG.Core.Retrieval;

namespace SunatRAG.Core.Agents;

/// <summary>Everything that belongs to ONE conversation: the agent, its citation numbering and the disposable providers.</summary>
public sealed class SunatAgentBundle(AIAgent agent, CitationRegistry citations, IReadOnlyList<IDisposable> disposables) : IDisposable
{
    public AIAgent Agent { get; } = agent;
    public CitationRegistry Citations { get; } = citations;

    public void Dispose()
    {
        foreach (var d in disposables) d.Dispose();
    }
}

/// <summary>
/// Composes the agent. A singleton: it only holds shared, stateless dependencies (the IChatClient pipeline from the composition root and the
/// retriever). Each conversation calls <see cref="Create"/> and gets its own agent, registry and tool, so nothing mutable is shared between tabs.
///
/// Anatomy of the agent that comes out:
///
///   ChatClientAgent
///     ├─ IChatClient          : the DI pipeline (function invocation -> OTel -> logging -> Ollama). The agent detects the existing
///     │                         FunctionInvokingChatClient and does not add a second one.
///     ├─ ChatOptions          : instructions, temperature, max output, reasoning effort, and (Tool strategy) the buscar_normativa AIFunction
///     ├─ AIContextProviders   : run before each model call, in order
///     │    ├─ RetrievalContextProvider   (ContextProvider strategy only)
///     │    ├─ AgentSkillsProvider        advertises skills, adds load_skill / read_skill_resource tools
///     │    └─ CompactionProvider         last, so it sees the final message list and trims it to the context window
///     └─ (default) InMemoryChatHistoryProvider keeps the transcript inside the AgentSession
///
/// and the whole agent is wrapped with OpenTelemetryAgent so agent runs appear as spans above the model-call spans.
/// </summary>
public sealed class SunatAgentFactory(
    IChatClient chat,
    IChunkRetriever retriever,
    IOptions<AgentOptions> agentOptions,
    IOptions<RetrievalOptions> retrievalOptions,
    IOptions<AiOptions> aiOptions,
    ILoggerFactory loggers)
{
    public SunatAgentBundle Create()
    {
        var o = agentOptions.Value;
        var registry = new CitationRegistry();
        var search = new SunatSearch(retriever, registry, agentOptions, loggers.CreateLogger<SunatSearch>());
        var disposables = new List<IDisposable>();
        var providers = new List<AIContextProvider>();

        var chatOptions = new ChatOptions
        {
            Temperature = o.Temperature,
            // Thinking tokens and answer tokens share one budget on a local model; ReservedAnswerTokens already accounts for both.
            MaxOutputTokens = retrievalOptions.Value.ReservedAnswerTokens,
            Reasoning = new ReasoningOptions { Effort = o.Reasoning }
        };

        if (o.Retrieval == RetrievalStrategy.ContextProvider)
            providers.Add(new RetrievalContextProvider(search));
        else
            chatOptions.Tools = [search.AsTool()];

        var skillsPath = ResolveSkillsPath(o);
        if (skillsPath is not null)
        {
            // load_skill / read_skill_resource REQUIRE human approval by default (AgentSkillsProviderOptions.Disable*Approval = false):
            // the run would stop with an approval request the UI never answers. These two tools only read files we ship, so approval is
            // disabled. We do NOT pass a script runner: skills here have no scripts, and run_skill_script is never exposed.
            var skills = new AgentSkillsProvider(
                skillsPath, null, null,
                new AgentSkillsProviderOptions { DisableLoadSkillApproval = true, DisableReadSkillResourceApproval = true },
                loggers);
            disposables.Add(skills);
            providers.Add(skills);
        }

        if (o.CompactHistory)
        {
            // Input budget = context window - output budget. Defaults: evict old tool results past 50 % of it, drop old turns past 80 %.
            // Compaction applies to what is sent to the model; the session transcript itself is not rewritten.
            var contextLength = aiOptions.Value.Ollama.ContextLength;
#pragma warning disable MAAI001 // Compaction is flagged experimental in Agent Framework 1.27: re-check the API on upgrade.
            providers.Add(new CompactionProvider(
                new ContextWindowCompactionStrategy(contextLength, retrievalOptions.Value.ReservedAnswerTokens), null, loggers));
#pragma warning restore MAAI001
        }

        chatOptions.Instructions = SunatInstructions.Build(o.Retrieval, skillsPath is not null);

        var inner = new ChatClientAgent(
            chat,
            new ChatClientAgentOptions
            {
                Name = "sunat-rag",
                Description = "Responde preguntas sobre normativa tributaria SUNAT con citas.",
                ChatOptions = chatOptions,
                AIContextProviders = providers
            },
            loggers);

        var agent = inner.AsBuilder().UseOpenTelemetry(AiServiceCollectionExtensions.TelemetrySourceName).Build();
        return new SunatAgentBundle(agent, registry, disposables);
    }

    private static string? ResolveSkillsPath(AgentOptions o)
    {
        if (!o.EnableSkills || string.IsNullOrWhiteSpace(o.SkillsPath)) return null;
        var path = Path.IsPathRooted(o.SkillsPath) ? o.SkillsPath : Path.Combine(AppContext.BaseDirectory, o.SkillsPath);
        return Directory.Exists(path) ? path : null;
    }
}
