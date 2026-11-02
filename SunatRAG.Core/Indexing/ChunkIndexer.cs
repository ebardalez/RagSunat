using System.Diagnostics;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.VectorData;
using SunatRAG.Core.Configuration;
using SunatRAG.Core.Documents;

namespace SunatRAG.Core.Indexing;

/// <param name="Rebuild">Drop and recreate both indexes first (use after changing dimensions or distance function).</param>
/// <param name="Prune">Remove documents that are in the index but no longer in the chunk set (e.g. now excluded as administrativas).</param>
public sealed record IndexRequest(bool Rebuild = false, bool Prune = false, int? DocumentLimit = null, string? OnlyDocumentId = null);

/// <summary>Reported after every stored batch.</summary>
public sealed record IndexProgress(int Batch, int Embedded, int ToEmbed, TimeSpan Elapsed)
{
    public TimeSpan? Eta => Embedded > 0 ? TimeSpan.FromSeconds(Elapsed.TotalSeconds / Embedded * (ToEmbed - Embedded)) : null;
}

public sealed record IndexingSummary(
    int Documents,
    int ChunksSeen,
    int ChunksEmbedded,
    int ChunksSkipped,
    int ChunksDeleted,
    int Batches,
    long TokensEmbedded,
    TimeSpan EmbedTime,
    TimeSpan StoreTime,
    TimeSpan Total,
    string EmbeddingModel)
{
    public double ChunksPerSecond => EmbedTime.TotalSeconds > 0 ? ChunksEmbedded / EmbedTime.TotalSeconds : 0;
    public double TokensPerSecond => EmbedTime.TotalSeconds > 0 ? TokensEmbedded / EmbedTime.TotalSeconds : 0;
}

/// <summary>
/// chunks -> embeddings -> SQLite (vec0 + FTS5), resumable and incremental.
///
/// Resumability, in one sentence: a chunk is "done" when the store already holds a record with the same ChunkId and the same
/// EmbeddingKey ({TextHash}|{model}|{recipe}); everything else is (re)embedded. Kill the process at any point, run it again,
/// and only the missing work is repeated (worst case: one batch).
///
/// Two passes: (1) per document, cheap and model-free: sync FTS5, delete ghost chunks, work out what needs a vector;
/// (2) embed the pending chunks in batches that span documents (a typical document has only ~6 chunks, so per-document
/// batches would waste most of each request's fixed overhead).
/// </summary>
public sealed class ChunkIndexer(
    VectorStoreCollection<string, ChunkRecord> collection,
    Fts5KeywordIndex keywords,
    IEmbeddingGenerator<string, Embedding<float>> embedder,
    IOptions<IndexOptions> indexOptions,
    IOptions<AiOptions> aiOptions,
    ILogger<ChunkIndexer> logger)
{
    private const int MaxChunksPerDocument = 100_000;
    private const int PerChunkHeaderTokens = 32; // title + heading prefix added by EmbeddingText

    public async Task EnsureReadyAsync(CancellationToken ct)
    {
        await collection.EnsureCollectionExistsAsync(ct);
        await keywords.EnsureCreatedAsync(ct);
    }

    public async Task<IndexingSummary> IndexAsync(
        IReadOnlyList<DocumentChunk> chunks, IndexRequest request, IProgress<IndexProgress>? progress, CancellationToken ct)
    {
        var options = indexOptions.Value;
        var dimensions = aiOptions.Value.Ollama.EmbeddingDimensions;
        // The abstraction exposes which model it wraps; fall back to config if a wrapper hides it.
        var model = embedder.GetService<EmbeddingGeneratorMetadata>()?.DefaultModelId
                    ?? aiOptions.Value.Ollama.EmbeddingModel;

        var total = Stopwatch.StartNew();
        if (request.Rebuild)
        {
            await collection.EnsureCollectionDeletedAsync(ct);
            await keywords.ResetAsync(ct);
        }

        await EnsureReadyAsync(ct);

        var groups = chunks
            .GroupBy(c => c.DocumentId)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Where(g => request.OnlyDocumentId is null || g.Key == request.OnlyDocumentId)
            .Take(request.DocumentLimit ?? int.MaxValue)
            .Select(g => (Id: g.Key, Chunks: g.OrderBy(c => c.Index).ToList()))
            .ToList();

        int seen = 0, skipped = 0, deleted = 0;
        long storeTicks = 0;
        var pending = new List<DocumentChunk>();

        // ── Pass 1: per document, no model involved ──────────────────────────────────────────────
        foreach (var (docId, docChunks) in groups)
        {
            ct.ThrowIfCancellationRequested();
            seen += docChunks.Count;

            // What does the store already hold for this document?
            var existing = new Dictionary<string, string>(StringComparer.Ordinal);
            await foreach (var r in collection.GetAsync(x => x.DocumentId == docId, MaxChunksPerDocument, cancellationToken: ct))
                existing[r.ChunkId] = r.EmbeddingKey;

            var sw = Stopwatch.GetTimestamp();

            // Keyword side: always in sync (cheap, no model).
            await keywords.ReplaceDocumentAsync(docId, docChunks, ct);

            // Chunks that disappeared (document re-chunked into fewer pieces) must not linger as ghosts.
            var current = docChunks.Select(c => c.ChunkId).ToHashSet(StringComparer.Ordinal);
            var stale = existing.Keys.Where(id => !current.Contains(id)).ToList();
            if (stale.Count > 0)
            {
                await collection.DeleteAsync(stale, ct);
                deleted += stale.Count;
            }

            storeTicks += Stopwatch.GetTimestamp() - sw;

            // Resumability: embed only what is missing or was computed from different input.
            var todo = docChunks.Where(c => !existing.TryGetValue(c.ChunkId, out var key)
                                            || key != ChunkRecordMapper.EmbeddingKey(c, model, options.EmbeddingRecipe)).ToList();
            skipped += docChunks.Count - todo.Count;
            pending.AddRange(todo);
        }

        // ── Pass 2: embed + store, in batches ────────────────────────────────────────────────────
        int embedded = 0, batches = 0;
        long tokens = 0, embedTicks = 0;
        var embedStart = Stopwatch.StartNew();

        foreach (var batch in EmbeddingBatcher.Batch(pending, options.EmbeddingBatchSize, options.EmbeddingBatchTokenBudget,
                     c => c.TokenCount + PerChunkHeaderTokens))
        {
            ct.ThrowIfCancellationRequested();
            var texts = batch.Select(ChunkRecordMapper.EmbeddingText).ToList();

            var sw = Stopwatch.GetTimestamp();
            var vectors = await EmbedWithRetryAsync(texts, ct);
            embedTicks += Stopwatch.GetTimestamp() - sw;

            if (vectors.Count != batch.Count)
                throw new InvalidOperationException($"El modelo devolvió {vectors.Count} vectores para {batch.Count} textos.");

            var records = new List<ChunkRecord>(batch.Count);
            for (var i = 0; i < batch.Count; i++)
            {
                var v = vectors[i].Vector;
                if (v.Length != dimensions)
                    throw new InvalidOperationException(
                        $"El modelo '{model}' devolvió {v.Length} dimensiones pero Ai:Ollama:EmbeddingDimensions = {dimensions}. " +
                        "Corrige la configuración y ejecuta con --rebuild.");
                records.Add(ChunkRecordMapper.ToRecord(
                    batch[i], ChunkRecordMapper.EmbeddingKey(batch[i], model, options.EmbeddingRecipe), v));
            }

            sw = Stopwatch.GetTimestamp();
            await collection.UpsertAsync(records, ct);
            storeTicks += Stopwatch.GetTimestamp() - sw;

            batches++;
            embedded += batch.Count;
            tokens += batch.Sum(c => (long)c.TokenCount);
            progress?.Report(new IndexProgress(batches, embedded, pending.Count, embedStart.Elapsed));
        }

        if (request.Prune && request.OnlyDocumentId is null && request.DocumentLimit is null)
            deleted += await PruneAsync(chunks.Select(c => c.DocumentId).ToHashSet(StringComparer.Ordinal), ct);

        total.Stop();
        logger.LogInformation("Indexado: {Embedded} embebidos, {Skipped} omitidos, {Deleted} eliminados en {Seconds:0.0} s.",
            embedded, skipped, deleted, total.Elapsed.TotalSeconds);

        return new IndexingSummary(groups.Count, seen, embedded, skipped, deleted, batches, tokens,
            Stopwatch.GetElapsedTime(0, embedTicks), Stopwatch.GetElapsedTime(0, storeTicks), total.Elapsed, model);
    }

    private async Task<int> PruneAsync(HashSet<string> keep, CancellationToken ct)
    {
        var removed = 0;
        foreach (var docId in await keywords.DocumentIdsAsync(ct))
        {
            if (keep.Contains(docId)) continue;

            var ids = new List<string>();
            await foreach (var r in collection.GetAsync(x => x.DocumentId == docId, MaxChunksPerDocument, cancellationToken: ct))
                ids.Add(r.ChunkId);
            if (ids.Count > 0) await collection.DeleteAsync(ids, ct);
            await keywords.DeleteDocumentAsync(docId, ct);
            removed += ids.Count;
        }

        return removed;
    }

    /// <summary>Retry only transport failures and 5xx; a bad request (4xx) or a dimension mismatch should fail loudly, not loop.</summary>
    private async Task<GeneratedEmbeddings<Embedding<float>>> EmbedWithRetryAsync(IReadOnlyList<string> texts, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await embedder.GenerateAsync(texts, cancellationToken: ct);
            }
            catch (HttpRequestException ex) when (attempt < 3 && (ex.StatusCode is null || ex.StatusCode >= System.Net.HttpStatusCode.InternalServerError))
            {
                logger.LogWarning(ex, "Embedding falló (intento {Attempt}/3); reintentando.", attempt);
                await Task.Delay(TimeSpan.FromSeconds(2 * attempt), ct);
            }
        }
    }
}

public static class EmbeddingBatcher
{
    /// <summary>
    /// Greedy batching under two limits: item count and an (approximate) token budget.
    /// A single item over budget still goes out alone; we never drop input.
    /// </summary>
    public static IEnumerable<IReadOnlyList<T>> Batch<T>(IEnumerable<T> items, int maxItems, int maxTokens, Func<T, int> tokensOf)
    {
        var current = new List<T>();
        var currentTokens = 0;
        foreach (var item in items)
        {
            var t = tokensOf(item);
            if (current.Count > 0 && (current.Count >= maxItems || currentTokens + t > maxTokens))
            {
                yield return current;
                current = [];
                currentTokens = 0;
            }

            current.Add(item);
            currentTokens += t;
        }

        if (current.Count > 0) yield return current;
    }
}
