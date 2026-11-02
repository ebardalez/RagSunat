using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SunatRAG.Core.Configuration;
using SunatRAG.Core.Retrieval;

namespace SunatRAG.Core.Composition;

/// <summary>
/// Registers retrieval. Requires AddSunatAi (IEmbeddingGenerator, IChatClient, AiOptions) and AddSunatIndex (collection + FTS5).
/// Nothing here names a provider: swapping Ollama for Azure OpenAI, or SQLite for Azure AI Search, changes the two composition
/// roots and leaves every class in Core.Retrieval untouched.
/// </summary>
public static class RetrievalServiceCollectionExtensions
{
    public static IServiceCollection AddSunatRetrieval(this IServiceCollection services, IConfiguration config)
    {
        services.AddOptions<RetrievalOptions>()
            .Bind(config.GetSection(RetrievalOptions.SectionName))
            .Validate(o => o.CandidatesPerRanker >= 1, "Retrieval:CandidatesPerRanker debe ser >= 1.")
            .Validate(o => o.RrfK >= 1, "Retrieval:RrfK debe ser >= 1.")
            .Validate(o => o.TopK >= 1 && o.MaxChunksPerDocument >= 1, "Retrieval:TopK y MaxChunksPerDocument deben ser >= 1.")
            .Validate(o => o.ChatTokenSafetyFactor >= 1.0, "Retrieval:ChatTokenSafetyFactor debe ser >= 1.0.")
            .ValidateOnStart();

        services.AddSingleton<LlmReranker>();
        services.AddSingleton<HybridRetriever>();
        services.AddSingleton<IChunkRetriever>(sp => sp.GetRequiredService<HybridRetriever>());
        return services;
    }
}
