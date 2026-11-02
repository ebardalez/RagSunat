using System.Collections.Concurrent;
using Microsoft.Extensions.AI;

namespace SunatRAG.Core.Evaluation;

/// <summary>
/// Embedding middleware that remembers what it already embedded. The eval sweeps run the same 28 queries through a dozen retriever
/// configurations; the query embedding is identical every time, so computing it once turns a ~2 minute sweep into ~10 seconds.
///
/// This is the embedding-side twin of IChatClient middleware: DelegatingEmbeddingGenerator forwards everything to the inner generator
/// (metadata, GetService, Dispose) and you override only what you change. It is also a good example of why the abstraction exists:
/// the retriever neither knows nor cares that a cache sits in front of Ollama.
/// </summary>
public sealed class MemoizingEmbeddingGenerator(IEmbeddingGenerator<string, Embedding<float>> inner)
    : DelegatingEmbeddingGenerator<string, Embedding<float>>(inner)
{
    private readonly ConcurrentDictionary<string, Embedding<float>> _cache = new(StringComparer.Ordinal);

    public int Hits { get; private set; }
    public int Misses { get; private set; }

    public override async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
    {
        var requested = values.ToList();
        var missing = requested.Where(v => !_cache.ContainsKey(v)).Distinct(StringComparer.Ordinal).ToList();
        Hits += requested.Count - missing.Count;
        Misses += missing.Count;

        if (missing.Count > 0)
        {
            var generated = await base.GenerateAsync(missing, options, cancellationToken);
            for (var i = 0; i < missing.Count; i++) _cache[missing[i]] = generated[i];
        }

        return new GeneratedEmbeddings<Embedding<float>>(requested.Select(v => _cache[v]).ToList());
    }
}
