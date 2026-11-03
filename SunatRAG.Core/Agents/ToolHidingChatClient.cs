using Microsoft.Extensions.AI;

namespace SunatRAG.Core.Agents;

/// <summary>
/// An IChatClient middleware that removes named tools from every request before they reach the rest of the pipeline.
///
/// WHY it exists: AgentSkillsProvider always advertises its three tools (load_skill, read_skill_resource, run_skill_script) and
/// <c>AgentSkillsProviderOptions</c> only lets you switch OFF the approval gate, not the tool. Our skills have no scripts and we pass no
/// runner, so run_skill_script could only ever end a run with an approval request nobody answers (the UI has no approval flow).
/// The provider adds its tools to ChatOptions, and ChatOptions is exactly what a DelegatingChatClient can edit: this is the
/// "middleware instead of subclassing" argument for MEAI in miniature. No framework type is touched, and the same class would hide
/// any tool from any provider.
///
/// It is placed OUTERMOST (around the DI pipeline), so the FunctionInvokingChatClient below it never sees the tool either: a tool the
/// model was never offered cannot be called. <see cref="DelegatingChatClient.GetService"/> forwards to the inner client, so the agent
/// still discovers the existing FunctionInvokingChatClient and does not add a second one.
/// </summary>
public sealed class ToolHidingChatClient(IChatClient inner, IEnumerable<string> hiddenToolNames) : DelegatingChatClient(inner)
{
    private readonly HashSet<string> _hidden = new(hiddenToolNames, StringComparer.Ordinal);

    public override Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        base.GetResponseAsync(messages, WithoutHiddenTools(options), cancellationToken);

    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        base.GetStreamingResponseAsync(messages, WithoutHiddenTools(options), cancellationToken);

    private ChatOptions? WithoutHiddenTools(ChatOptions? options)
    {
        if (options?.Tools is not { Count: > 0 } tools || !tools.Any(t => _hidden.Contains(t.Name))) return options;

        // Clone: ChatOptions instances can be shared across calls (the agent reuses its own), so never mutate the caller's.
        var clone = options.Clone();
        clone.Tools = [.. tools.Where(t => !_hidden.Contains(t.Name))];
        return clone;
    }
}
