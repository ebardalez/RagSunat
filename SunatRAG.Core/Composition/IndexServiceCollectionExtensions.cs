using CommunityToolkit.VectorData.SqliteVec;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SunatRAG.Core.Configuration;
using SunatRAG.Core.Indexing;

namespace SunatRAG.Core.Composition;

/// <summary>
/// Composition root for the vector store. Together with AiServiceCollectionExtensions these are the only files that name a
/// concrete provider: everything else depends on VectorStoreCollection&lt;string, ChunkRecord&gt; (an abstract class from
/// Microsoft.Extensions.VectorData.Abstractions) and IEmbeddingGenerator. Requires AddSunatAi (dimensions + embedder).
/// </summary>
public static class IndexServiceCollectionExtensions
{
    public static IServiceCollection AddSunatIndex(this IServiceCollection services, IConfiguration config)
    {
        services.AddOptions<IndexOptions>()
            .Bind(config.GetSection(IndexOptions.SectionName))
            .Validate(o => !string.IsNullOrWhiteSpace(o.DatabasePath), "Index:DatabasePath es obligatorio.")
            .Validate(o => !string.IsNullOrWhiteSpace(o.CollectionName), "Index:CollectionName es obligatorio.")
            .Validate(o => o.EmbeddingBatchSize is >= 1 and <= 128, "Index:EmbeddingBatchSize debe estar entre 1 y 128.")
            .Validate(o => o.EmbeddingBatchTokenBudget >= 500, "Index:EmbeddingBatchTokenBudget debe ser >= 500.")
            .ValidateOnStart();

        // Registers VectorStoreCollection<string, ChunkRecord> (+ its search interface). To swap the store, replace this call:
        // nothing downstream sees SqliteCollection.
        services.AddSqliteCollection<string, ChunkRecord>(
            config.GetSection(IndexOptions.SectionName)["CollectionName"] ?? new IndexOptions().CollectionName,
            sp => SqliteConnectionStrings.Build(sp.GetRequiredService<IOptions<IndexOptions>>().Value),
            sp => new SqliteCollectionOptions
            {
                Definition = ChunkCollection.CreateDefinition(
                    sp.GetRequiredService<IOptions<AiOptions>>().Value.Ollama.EmbeddingDimensions)
            });

        services.AddSingleton<Fts5KeywordIndex>();
        services.AddSingleton<ChunkIndexer>();
        return services;
    }
}
