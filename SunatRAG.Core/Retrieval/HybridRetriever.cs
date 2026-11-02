using System.Diagnostics;
using System.Linq.Expressions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.VectorData;
using SunatRAG.Core.Configuration;
using SunatRAG.Core.Indexing;

namespace SunatRAG.Core.Retrieval;

/// <summary>
/// Hybrid retrieval over the SQLite index: dense (sqlite-vec) + BM25 (FTS5) -> Reciprocal Rank Fusion -> optional LLM rerank
/// -> diversity cap and token budget -> chunks with everything a citation needs.
///
/// Depends only on abstractions: VectorStoreCollection (Microsoft.Extensions.VectorData), IEmbeddingGenerator and
/// (optionally) IChatClient through <see cref="LlmReranker"/>. No SQLite-vec or OllamaSharp type appears here.
///
/// The two rankers run concurrently: the dense side is "embed the query (network to Ollama) then scan vectors",
/// the keyword side is a local FTS5 query, so BM25 finishes while the embedding is still in flight.
/// </summary>
public sealed class HybridRetriever(
    VectorStoreCollection<string, ChunkRecord> collection,
    Fts5KeywordIndex keywords,
    IEmbeddingGenerator<string, Embedding<float>> embedder,
    IOptions<RetrievalOptions> retrievalOptions,
    IOptions<AiOptions> aiOptions,
    ILogger<HybridRetriever> logger,
    LlmReranker? reranker = null)
{
    private sealed record Hit(ChunkRecord Record, double? Distance, double? Bm25);

    private sealed record Side(IReadOnlyList<Hit> Hits, TimeSpan Search, TimeSpan Embed)
    {
        public static Side Empty { get; } = new([], TimeSpan.Zero, TimeSpan.Zero);
    }

    public async Task<RetrievalResult> RetrieveAsync(RetrievalRequest request, CancellationToken ct = default)
    {
        var o = retrievalOptions.Value;
        var query = request.Query.Trim();
        if (query.Length == 0) throw new ArgumentException("La consulta está vacía.", nameof(request));

        var filters = request.Filters ?? new SearchFilters();
        var pushdown = filters.ToPushdownPredicate();
        var post = filters.ToPostFilter();
        var keep = o.CandidatesPerRanker;
        var fetch = keep * Math.Max(1, o.FilterOverfetch);

        var vectorTask = request.Mode == RetrievalMode.KeywordOnly
            ? Task.FromResult(Side.Empty)
            : SearchVectorAsync(query, pushdown, post, keep, fetch, ct);
        var keywordTask = request.Mode == RetrievalMode.VectorOnly
            ? Task.FromResult(Side.Empty)
            : SearchKeywordAsync(query, pushdown, post, keep, fetch, ct);
        await Task.WhenAll(vectorTask, keywordTask);
        var vector = await vectorTask;
        var keyword = await keywordTask;

        // ── Fusion ───────────────────────────────────────────────────────────────────────
        var byId = new Dictionary<string, Hit>(StringComparer.Ordinal);
        foreach (var h in vector.Hits) byId[h.Record.ChunkId] = h;
        foreach (var h in keyword.Hits)
            byId[h.Record.ChunkId] = byId.TryGetValue(h.Record.ChunkId, out var v) ? v with { Bm25 = h.Bm25 } : h;

        var fused = RankFusion.ReciprocalRank(
            vector.Hits.Select(h => h.Record.ChunkId).ToList(),
            keyword.Hits.Select(h => h.Record.ChunkId).ToList(),
            o.RrfK, o.VectorWeight, o.KeywordWeight);

        var candidates = fused.Select(f =>
        {
            var hit = byId[f.Key];
            return new RetrievedChunk(hit.Record, f.Score, f.VectorRank, f.KeywordRank, hit.Distance, hit.Bm25,
                ContextBudget.ChunkCost(hit.Record.TokenCount, o));
        }).ToList();

        // ── Optional LLM rerank of the head of the list ──────────────────────────────────
        var rerankTime = TimeSpan.Zero;
        bool rerankApplied = false, rerankFellBack = false;
        if (request.Rerank == RerankMode.Llm)
        {
            if (reranker is null)
                throw new InvalidOperationException("Se pidió rerank con LLM pero LlmReranker no está registrado (AddSunatRetrieval).");

            var head = candidates.Take(o.RerankCandidates).ToList();
            var outcome = await reranker.RerankAsync(query, head.Select(c => c.Record).ToList(), ct);
            rerankTime = outcome.Elapsed;
            rerankFellBack = outcome.FellBack;
            rerankApplied = !outcome.FellBack;
            candidates = outcome.Order.Select(i => head[i]).Concat(candidates.Skip(head.Count)).ToList();
        }

        // ── Diversity cap + token budget ─────────────────────────────────────────────────
        var contextLength = aiOptions.Value.Ollama.ContextLength;
        var budget = ContextBudget.Available(contextLength, o, ContextBudget.EstimateQuestionTokens(query));
        var selection = ContextSelector.Select(
            candidates, request.TopK ?? o.TopK, o.MaxChunksPerDocument, budget, c => c.Record.DocumentId, c => c.ContextTokens);

        var best = vector.Hits.Where(h => h.Distance is not null).Select(h => h.Distance!.Value).DefaultIfEmpty(double.NaN).Min();
        var diagnostics = new RetrievalDiagnostics(
            request.Mode, vector.Embed, vector.Search, keyword.Search, rerankTime,
            vector.Hits.Count, keyword.Hits.Count, fused.Count,
            double.IsNaN(best) ? null : best,
            contextLength, budget, selection.UsedTokens, selection.SkippedForDocumentCap, selection.SkippedForBudget,
            rerankApplied, rerankFellBack);

        logger.LogInformation(
            "Retrieval {Mode}: {Vec} vectoriales + {Kw} BM25 -> {Fused} fusionados -> {Selected} en contexto ({Used}/{Budget} tokens).",
            request.Mode, vector.Hits.Count, keyword.Hits.Count, fused.Count, selection.Selected.Count, selection.UsedTokens, budget);

        return new RetrievalResult(query, selection.Selected, diagnostics);
    }

    private async Task<Side> SearchVectorAsync(
        string query, Expression<Func<ChunkRecord, bool>>? pushdown, Func<ChunkRecord, bool>? post, int keep, int fetch, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        // The query goes through the SAME embedder (and the same model) as the documents; bge-m3 needs no query/passage prefix.
        var vector = await embedder.GenerateVectorAsync(query, cancellationToken: ct);
        var embed = sw.Elapsed;

        sw.Restart();
        var hits = new List<Hit>();
        var options = new VectorSearchOptions<ChunkRecord> { Filter = pushdown };
        await foreach (var result in collection.SearchAsync(vector, fetch, options, ct))
        {
            if (post is not null && !post(result.Record)) continue;
            hits.Add(new Hit(result.Record, result.Score, null));
            if (hits.Count == keep) break;
        }

        return new Side(hits, sw.Elapsed, embed);
    }

    private async Task<Side> SearchKeywordAsync(
        string query, Expression<Func<ChunkRecord, bool>>? pushdown, Func<ChunkRecord, bool>? post, int keep, int fetch, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var raw = new List<KeywordHit>();
        await foreach (var h in keywords.SearchAsync(query, fetch, ct)) raw.Add(h);
        if (raw.Count == 0) return new Side([], sw.Elapsed, TimeSpan.Zero);

        // FTS5 only knows ids and text. Hydrate the records, then apply the same filters the vector side got.
        var records = new Dictionary<string, ChunkRecord>(StringComparer.Ordinal);
        await foreach (var r in collection.GetAsync(raw.Select(h => h.ChunkId), cancellationToken: ct)) records[r.ChunkId] = r;

        var predicate = pushdown?.Compile();
        var hits = new List<Hit>();
        foreach (var h in raw) // BM25 order is the ranking; keep it
        {
            if (!records.TryGetValue(h.ChunkId, out var record))
            {
                logger.LogWarning("El chunk {ChunkId} está en FTS5 pero no en el almacén vectorial: índices desincronizados (ejecuta `index`).", h.ChunkId);
                continue;
            }

            if (predicate is not null && !predicate(record)) continue;
            if (post is not null && !post(record)) continue;

            hits.Add(new Hit(record, null, h.Score));
            if (hits.Count == keep) break;
        }

        return new Side(hits, sw.Elapsed, TimeSpan.Zero);
    }
}
