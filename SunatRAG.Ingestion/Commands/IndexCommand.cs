using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.VectorData;
using SunatRAG.Core.Configuration;
using SunatRAG.Core.Indexing;
using SunatRAG.Ingestion.Indexing;
using SunatRAG.Ingestion.Scraping;

namespace SunatRAG.Ingestion.Commands;

/// <summary>Phase 3: chunks.jsonl -> embeddings -> SQLite (vec0 + FTS5). Also `--probe` to sanity-check the result.</summary>
public static class IndexCommand
{
    public const string Usage =
        "Uso: dotnet run --project SunatRAG.Ingestion -- index [--rebuild] [--prune] [--only <id>] [--limit N] [--batch N]\n" +
        "     dotnet run --project SunatRAG.Ingestion -- index --probe \"texto de la consulta\" [--top N]";

    public static async Task<int> RunAsync(IServiceProvider sp, string[] args, CancellationToken ct)
    {
        var rebuild = false;
        var prune = false;
        string? only = null, probe = null;
        int? limit = null, batch = null;
        var top = 5;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--rebuild": rebuild = true; break;
                case "--prune": prune = true; break;
                case "--only" when i + 1 < args.Length: only = args[++i]; break;
                case "--probe" when i + 1 < args.Length: probe = args[++i]; break;
                case "--limit" when i + 1 < args.Length && int.TryParse(args[i + 1], out var l): limit = l; i++; break;
                case "--batch" when i + 1 < args.Length && int.TryParse(args[i + 1], out var b): batch = b; i++; break;
                case "--top" when i + 1 < args.Length && int.TryParse(args[i + 1], out var t): top = t; i++; break;
                default:
                    Console.Error.WriteLine($"Argumento no válido: {args[i]}");
                    Console.Error.WriteLine(Usage);
                    return 1;
            }
        }

        Console.OutputEncoding = System.Text.Encoding.UTF8;
        return probe is not null ? await ProbeAsync(sp, probe, top, ct) : await IndexAsync(sp, rebuild, prune, only, limit, batch, ct);
    }

    private static async Task<int> IndexAsync(IServiceProvider sp, bool rebuild, bool prune, string? only, int? limit, int? batch, CancellationToken ct)
    {
        var paths = sp.GetRequiredService<DataPaths>();
        var chunksFile = Path.Combine(paths.Root, "chunks.jsonl");
        var chunks = await ChunkFile.ReadAsync(chunksFile, ct);
        var options = sp.GetRequiredService<IOptions<IndexOptions>>().Value;
        var ai = sp.GetRequiredService<IOptions<AiOptions>>().Value.Ollama;

        if (batch is { } b) options = new IndexOptions
        {
            DatabasePath = options.DatabasePath, CollectionName = options.CollectionName, EmbeddingBatchSize = b,
            EmbeddingBatchTokenBudget = options.EmbeddingBatchTokenBudget, EmbeddingRecipe = options.EmbeddingRecipe
        };

        Console.WriteLine($"Chunks leídos: {chunks.Count:N0} en {chunks.Select(c => c.DocumentId).Distinct().Count()} documentos ({chunksFile}).");
        Console.WriteLine($"Modelo: {ai.EmbeddingModel} @ {ai.Endpoint} | dims {ai.EmbeddingDimensions} | lote ≤{options.EmbeddingBatchSize} chunks / ≤{options.EmbeddingBatchTokenBudget} tokens.");
        Console.WriteLine($"Base de datos: {Path.GetFullPath(options.DatabasePath)}");
        if (rebuild) Console.WriteLine("--rebuild: se descarta el índice existente.");

        // Token math for the batch limit: show it so the numbers are not magic.
        var tokens = chunks.Select(c => (long)c.TokenCount).Sum();
        Console.WriteLine($"Tokens totales a embeber (estimado por el tokenizador de la Fase 2): {tokens:N0}; " +
                          $"≈ {Math.Ceiling(tokens / (double)options.EmbeddingBatchTokenBudget):N0} lotes como mínimo.");

        var indexer = sp.GetRequiredService<ChunkIndexer>();
        if (batch is not null)
        {
            // Rebuild the indexer with the overridden options (singleton registered with config values).
            indexer = ActivatorUtilities.CreateInstance<ChunkIndexer>(sp, Options.Create(options));
        }

        var progress = new Progress<IndexProgress>(p =>
            Console.WriteLine($"  lote {p.Batch,4} | {p.Embedded,5}/{p.ToEmbed} chunks ({p.Embedded * 100 / p.ToEmbed,3} %) | " +
                              $"{p.Elapsed:mm\\:ss} transcurrido | ETA {(p.Eta is { } eta ? eta.ToString("hh\\:mm\\:ss") : "?")}"));
        var summary = await indexer.IndexAsync(chunks, new IndexRequest(rebuild, prune, limit, only), progress, ct);
        PrintSummary(summary);

        var fts = await sp.GetRequiredService<Fts5KeywordIndex>().CountAsync(ct);
        Console.WriteLine($"Filas FTS5: {fts:N0}");
        return 0;
    }

    private static void PrintSummary(IndexingSummary s)
    {
        Console.WriteLine();
        Console.WriteLine("── Resumen ──────────────────────────────");
        Console.WriteLine($"Documentos: {s.Documents} | chunks vistos: {s.ChunksSeen:N0}");
        Console.WriteLine($"Embebidos: {s.ChunksEmbedded:N0} | omitidos (ya indexados): {s.ChunksSkipped:N0} | eliminados: {s.ChunksDeleted:N0}");
        if (s.ChunksEmbedded > 0)
        {
            Console.WriteLine($"Lotes: {s.Batches} | media {(double)s.ChunksEmbedded / s.Batches:0.0} chunks/lote");
            Console.WriteLine($"Embeddings: {s.EmbedTime.TotalSeconds:0.0} s → {s.ChunksPerSecond:0.00} chunks/s, {s.TokensPerSecond:0} tokens/s");
            Console.WriteLine($"Almacenamiento (SQLite): {s.StoreTime.TotalSeconds:0.0} s");
        }

        Console.WriteLine($"Total: {s.Total.TotalSeconds:0.0} s | modelo: {s.EmbeddingModel}");
    }

    private static async Task<int> ProbeAsync(IServiceProvider sp, string query, int top, CancellationToken ct)
    {
        var collection = sp.GetRequiredService<VectorStoreCollection<string, ChunkRecord>>();
        var keywords = sp.GetRequiredService<Fts5KeywordIndex>();
        var embedder = sp.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>();

        if (!await collection.CollectionExistsAsync(ct))
        {
            Console.Error.WriteLine("El índice no existe todavía. Ejecuta `index` primero.");
            return 1;
        }

        Console.WriteLine($"Consulta: \"{query}\"");
        Console.WriteLine($"FTS5 MATCH: {Fts5KeywordIndex.BuildMatchQuery(query) ?? "(vacío)"}");

        var started = DateTime.UtcNow;
        var vector = await embedder.GenerateVectorAsync(query, cancellationToken: ct);
        Console.WriteLine($"Embedding de la consulta: {(DateTime.UtcNow - started).TotalMilliseconds:0} ms");

        Console.WriteLine();
        Console.WriteLine($"── Vectorial (distancia coseno, menor = más cercano) top {top} ──");
        var rank = 0;
        await foreach (var hit in collection.SearchAsync(vector, top, cancellationToken: ct))
            Print(++rank, hit.Score, hit.Record);

        Console.WriteLine();
        Console.WriteLine($"── Palabras clave (BM25, mayor = mejor) top {top} ──");
        var hits = new List<KeywordHit>();
        await foreach (var k in keywords.SearchAsync(query, top, ct)) hits.Add(k);
        var records = hits.Count == 0
            ? new Dictionary<string, ChunkRecord>()
            : await ToDictionaryAsync(collection.GetAsync(hits.Select(h => h.ChunkId), cancellationToken: ct));
        rank = 0;
        foreach (var h in hits)
        {
            if (records.TryGetValue(h.ChunkId, out var r)) Print(++rank, h.Score, r);
            else Console.WriteLine($"  {++rank}. {h.Score:0.000}  {h.ChunkId} (sin registro vectorial: índices desincronizados)");
        }

        return 0;
    }

    private static async Task<Dictionary<string, ChunkRecord>> ToDictionaryAsync(IAsyncEnumerable<ChunkRecord> source)
    {
        var d = new Dictionary<string, ChunkRecord>(StringComparer.Ordinal);
        await foreach (var r in source) d[r.ChunkId] = r;
        return d;
    }

    private static void Print(int rank, double? score, ChunkRecord r)
    {
        var text = r.Text.ReplaceLineEndings(" ");
        Console.WriteLine($"  {rank}. {score:0.000}  {r.Cita()}");
        Console.WriteLine($"       {(text.Length <= 160 ? text : text[..160] + "…")}");
    }
}
