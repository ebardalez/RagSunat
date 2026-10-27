using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OllamaSharp;
using SunatRAG.Core.Configuration;
using SunatRAG.Core.Health;

namespace SunatRAG.Core.Composition;

/// <summary>
/// Composition root for AI services. This is the ONLY file that references OllamaSharp.
/// </summary>
public static class AiServiceCollectionExtensions
{
    public const string OllamaHttpClientName = "ollama";
    public const string TelemetrySourceName = "SunatRAG";

    public static IServiceCollection AddSunatAi(this IServiceCollection services, IConfiguration config)
    {
        var section = config.GetSection(AiOptions.SectionName);
        services.AddOptions<AiOptions>()
            .Bind(section)
            .Validate(o => o.Provider != "Ollama" || Uri.TryCreate(o.Ollama.Endpoint, UriKind.Absolute, out _),
                "Ai:Ollama:Endpoint must be an absolute URI.")
            .Validate(o => o.Provider != "Ollama" || o.Ollama.RequestTimeout >= TimeSpan.FromMinutes(5),
                "Ai:Ollama:RequestTimeout must be >= 5 minutes for local models.")
            .ValidateOnStart();

        var ai = section.Get<AiOptions>() ?? new AiOptions();

        services.AddHealthChecks().AddCheck<OllamaHealthCheck>("ollama", tags: ["ready"]);

        return ai.Provider switch
        {
            "Ollama" => services.AddOllama(ai.Ollama),
            _ => throw new NotSupportedException($"Unknown Ai:Provider '{ai.Provider}'.")
        };
    }

    private static IServiceCollection AddOllama(this IServiceCollection services, OllamaOptions o)
    {
        // Explicit timeout: the default 100 s HttpClient timeout kills long local generations.
        services.AddHttpClient(OllamaHttpClientName, c =>
        {
            c.BaseAddress = new Uri(o.Endpoint);
            c.Timeout = o.RequestTimeout;
        });

        // First Use* = outermost. FunctionInvocation wraps the loop; OTel/logging see each round-trip.
        services.AddChatClient(sp =>
                new OllamaApiClient(
                    sp.GetRequiredService<IHttpClientFactory>().CreateClient(OllamaHttpClientName),
                    o.ChatModel))
            .UseFunctionInvocation()
            .UseOpenTelemetry(sourceName: TelemetrySourceName)
            .UseLogging();

        services.AddEmbeddingGenerator(sp =>
                (IEmbeddingGenerator<string, Embedding<float>>)new OllamaApiClient(
                    sp.GetRequiredService<IHttpClientFactory>().CreateClient(OllamaHttpClientName),
                    o.EmbeddingModel))
            .UseOpenTelemetry(sourceName: TelemetrySourceName)
            .UseLogging();

        return services;
    }
}
