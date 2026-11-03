using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace SunatRAG.Core.Agents;

/// <summary>What the UI needs from a streaming run. The UI never sees Agent Framework or MEAI types.</summary>
public abstract record ChatEvent;

/// <summary>A piece of the answer text.</summary>
public sealed record TextDelta(string Text) : ChatEvent;

/// <summary>The model asked for a tool: buscar_normativa, load_skill, read_skill_resource...</summary>
public sealed record ToolCalled(string Name, string? Arguments) : ChatEvent;

/// <summary>Token usage of one model call, as the server counted it (Ollama's prompt_eval_count / eval_count).</summary>
public sealed record UsageReported(long? InputTokens, long? OutputTokens) : ChatEvent;

/// <summary>
/// One conversation (in Blazor: one circuit = one browser tab, so register it Scoped). Owns the agent, its AgentSession and the citation numbering.
///
/// AgentSession is the conversation state (transcript + provider state). The AGENT is stateless between runs; the session is what you pass
/// back in. That separation is why one agent definition can serve many conversations and why a session can be serialized and resumed.
/// </summary>
public sealed class SunatChatSession(SunatAgentFactory factory) : IDisposable
{
    private SunatAgentBundle _bundle = factory.Create();
    private AgentSession? _session;

    public CitationRegistry Citations => _bundle.Citations;

    public async IAsyncEnumerable<ChatEvent> AskAsync(string question, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var answer = new System.Text.StringBuilder();
        await foreach (var ev in StreamAsync(question, ct))
        {
            if (ev is TextDelta d) answer.Append(d.Text);
            yield return ev;
        }

        // Reached only when the stream completed (a cancelled run throws before this line): auditing half an answer would mislead.
        var audit = AnswerAuditor.Audit(question, answer.ToString(), Citations, _bundle.Instructions);

        // Output guard: the model reproduced its own instructions. The text already streamed to the screen, so tell the consumer to replace it.
        if (audit.LeaksInstructions) yield return new AnswerReplaced(SunatInstructions.WithheldMessage);

        yield return new AuditReported(audit);
    }

    private async IAsyncEnumerable<ChatEvent> StreamAsync(string question, [EnumeratorCancellation] CancellationToken ct)
    {
        _session ??= await _bundle.Agent.CreateSessionAsync(ct);

        await foreach (var update in _bundle.Agent.RunStreamingAsync(question, _session, cancellationToken: ct))
        {
            foreach (var content in update.Contents)
            {
                switch (content)
                {
                    case FunctionCallContent call:
                        yield return new ToolCalled(call.Name, call.Arguments is null ? null : JsonSerializer.Serialize(call.Arguments));
                        break;
                    case UsageContent usage:
                        yield return new UsageReported(usage.Details.InputTokenCount, usage.Details.OutputTokenCount);
                        break;
                }
            }

            // AgentResponseUpdate.Text concatenates only the TextContent items (reasoning and tool traffic are separate content types).
            if (!string.IsNullOrEmpty(update.Text)) yield return new TextDelta(update.Text);
        }
    }

    /// <summary>New conversation: fresh session, fresh agent, citation numbers start again at [1].</summary>
    public void Reset()
    {
        _bundle.Dispose();
        _bundle = factory.Create();
        _session = null;
    }

    public void Dispose() => _bundle.Dispose();
}
