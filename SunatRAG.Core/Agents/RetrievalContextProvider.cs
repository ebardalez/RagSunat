using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace SunatRAG.Core.Agents;

/// <summary>
/// The alternative to the tool: RAG as an <see cref="AIContextProvider"/>.
///
/// An AIContextProvider is called by the agent BEFORE each model call and may add instructions, messages and tools to that call.
/// Here it searches with the user's last message and returns the numbered sources as <c>Instructions</c>.
///
/// Why Instructions and not Messages: AIContext.Messages "may become permanent additions to the conversation history", AIContext.Instructions
/// are transient. Retrieved chunks are ~8k tokens; stored in history they would be re-sent (and re-paid for) on every later turn.
/// As transient instructions they exist only for the call that needs them, and follow-up questions retrieve afresh.
///
/// Trade-offs vs the tool (measure them in Phase 6): one model call instead of two, and no dependence on tool-calling quality; but the query
/// is the raw user text (no rewriting), and every message triggers a search, including "gracias".
/// </summary>
public sealed class RetrievalContextProvider(SunatSearch search) : AIContextProvider
{
    protected override async ValueTask<AIContext> ProvideAIContextAsync(InvokingContext context, CancellationToken cancellationToken = default)
    {
        var question = context.AIContext.Messages?.LastOrDefault(m => m.Role == ChatRole.User)?.Text;
        if (string.IsNullOrWhiteSpace(question)) return new AIContext();

        var outcome = await search.SearchAsync(question, tipo: null, anio: null, cancellationToken);
        return new AIContext
        {
            Instructions = "FUENTES RECUPERADAS PARA LA ÚLTIMA PREGUNTA DEL USUARIO (cítalas como [n]; no hay otras):\n\n" + outcome.Text
        };
    }
}
