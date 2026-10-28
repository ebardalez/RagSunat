using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace SunatRAG.Ingestion.Extraction;

public static class ExtractionServiceCollectionExtensions
{
    /// <summary>Requires AddSunatScraping (DataPaths + ManifestStore).</summary>
    public static IServiceCollection AddSunatExtraction(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<ChunkingOptions>()
            .Bind(configuration.GetSection(ChunkingOptions.SectionName))
            .Validate(o => o.TargetTokens is >= 100 and <= 2000, "Chunking:TargetTokens debe estar entre 100 y 2000.")
            .Validate(o => o.OverlapTokens >= 0 && o.OverlapTokens <= o.TargetTokens / 2,
                "Chunking:OverlapTokens debe estar entre 0 y la mitad de TargetTokens.");

        services.AddSingleton(new PdfTextExtractor());
        services.AddSingleton<ChunkingRunner>();

        // Resolved lazily: only commands that chunk need the model file.
        services.AddSingleton<ITokenCounter>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<ChunkingOptions>>().Value;
            var path = Path.GetFullPath(options.TokenizerModelPath);
            if (File.Exists(path)) return new SentencePieceTokenCounter(path);

            if (options.AllowApproximateTokenizer)
            {
                sp.GetRequiredService<ILogger<ApproximateTokenCounter>>()
                    .LogWarning("No existe {Path}; se usa un estimado de tokens (chars/3.5). Solo para desarrollo.", path);
                return new ApproximateTokenCounter();
            }

            throw new FileNotFoundException(
                $"Falta el modelo del tokenizador de bge-m3 en '{path}'. Descárgalo (ver README de la Fase 2) " +
                "o usa --approx-tokens para una corrida de prueba.", path);
        });

        services.AddSingleton(sp =>
            new TokenAwareChunker(sp.GetRequiredService<ITokenCounter>(), sp.GetRequiredService<IOptions<ChunkingOptions>>().Value));

        return services;
    }
}
