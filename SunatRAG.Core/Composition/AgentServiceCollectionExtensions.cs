using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SunatRAG.Core.Agents;
using SunatRAG.Core.Configuration;

namespace SunatRAG.Core.Composition;

/// <summary>
/// Registers the Phase 5 agent. Requires AddSunatAi (IChatClient) and AddSunatRetrieval (IChunkRetriever).
/// Like retrieval, nothing here names a provider: the agent sees an IChatClient, so Ollama -> Azure OpenAI stays a change in
/// AiServiceCollectionExtensions only.
/// </summary>
public static class AgentServiceCollectionExtensions
{
    public static IServiceCollection AddSunatAgent(this IServiceCollection services, IConfiguration config)
    {
        services.AddOptions<AgentOptions>()
            .Bind(config.GetSection(AgentOptions.SectionName))
            .Validate(o => o.WeakMatchDistance is > 0 and <= 2, "Agent:WeakMatchDistance debe estar entre 0 y 2 (distancia coseno).")
            .Validate(o => o.Temperature is >= 0 and <= 2, "Agent:Temperature debe estar entre 0 y 2.")
            .ValidateOnStart();

        // Singleton factory (stateless), Scoped session: in Blazor Server a scope is a circuit, so each browser tab is one conversation.
        services.AddSingleton<SunatAgentFactory>();
        services.AddScoped<SunatChatSession>();
        return services;
    }
}
