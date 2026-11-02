namespace SunatRAG.Core.Configuration;

/// <summary>Settings for the SQLite index (vectors + FTS5 live in the same database file).</summary>
public sealed class IndexOptions
{
    public const string SectionName = "Index";

    /// <summary>Relative paths resolve against the current directory (run from the repo root, like Scraping:DataDirectory).</summary>
    public string DatabasePath { get; init; } = "data/sunat.db";

    public string CollectionName { get; init; } = "chunks";

    /// <summary>Max chunks per /api/embed request.</summary>
    public int EmbeddingBatchSize { get; init; } = 16;

    /// <summary>
    /// Max (approximate) tokens per /api/embed request. A batch is closed when either limit is reached.
    /// Keeps one request well inside the server's context/batch limits and makes the cost of a failed request bounded.
    /// </summary>
    public int EmbeddingBatchTokenBudget { get; init; } = 6000;

    /// <summary>
    /// Version of the "what text do we embed" recipe (see ChunkRecordMapper.EmbeddingText). Bump it when the recipe
    /// changes: every chunk is then re-embedded on the next run, because the stored EmbeddingKey no longer matches.
    /// </summary>
    public string EmbeddingRecipe { get; init; } = "v1";
}
