using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.VectorData;
using SunatRAG.Core.Composition;
using SunatRAG.Core.Configuration;
using SunatRAG.Core.Documents;
using SunatRAG.Core.Indexing;

namespace SunatRAG.Tests.Indexing;

/// <summary>Deterministic 8-dim "embedding": hashed bag of words, L2-normalised. Counts calls so tests can prove what was (not) re-embedded.</summary>
internal sealed class FakeEmbedder(int dimensions = 8) : IEmbeddingGenerator<string, Embedding<float>>
{
    public List<string> Embedded { get; } = [];
    public int Calls { get; private set; }

    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
    {
        Calls++;
        var result = new GeneratedEmbeddings<Embedding<float>>();
        foreach (var v in values)
        {
            Embedded.Add(v);
            result.Add(new Embedding<float>(Vectorize(v, dimensions)));
        }

        return Task.FromResult(result);
    }

    public static float[] Vectorize(string text, int dims)
    {
        var v = new float[dims];
        foreach (var word in text.ToLowerInvariant().Split([' ', '\n', '—', '.', ','], StringSplitOptions.RemoveEmptyEntries))
            v[SHA256.HashData(Encoding.UTF8.GetBytes(word))[0] % dims] += 1;
        var norm = MathF.Sqrt(v.Sum(x => x * x));
        if (norm == 0) v[0] = 1; else for (var i = 0; i < dims; i++) v[i] /= norm;
        return v;
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceType == typeof(EmbeddingGeneratorMetadata) ? new EmbeddingGeneratorMetadata("fake", defaultModelId: "fake-model") : null;

    public void Dispose() { }
}

public sealed class IndexingTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sunatrag-tests-" + Guid.NewGuid().ToString("N"));
    private readonly FakeEmbedder _embedder = new();
    private readonly ServiceProvider _sp;

    public IndexingTests()
    {
        Directory.CreateDirectory(_dir);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Ai:Ollama:Endpoint"] = "http://ollama.test:11434",
            ["Ai:Ollama:EmbeddingDimensions"] = "8",
            ["Index:DatabasePath"] = Path.Combine(_dir, "test.db"),
            ["Index:EmbeddingBatchSize"] = "2",
        }).Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.Configure<AiOptions>(config.GetSection("Ai"));
        services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(_embedder);
        services.AddSunatIndex(config);
        _sp = services.BuildServiceProvider();
    }

    private ChunkIndexer Indexer => _sp.GetRequiredService<ChunkIndexer>();
    private VectorStoreCollection<string, ChunkRecord> Collection => _sp.GetRequiredService<VectorStoreCollection<string, ChunkRecord>>();
    private Fts5KeywordIndex Keywords => _sp.GetRequiredService<Fts5KeywordIndex>();

    private static DocumentChunk Chunk(string doc, int index, string text, string? heading = null,
        Categoria cat = Categoria.Normativa, DateOnly? fecha = null) => new(
        ChunkId: $"{doc}#{index:D4}", DocumentId: doc, Index: index, Text: text, TokenCount: 50,
        TextHash: Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16],
        PageStart: 1, PageEnd: 1, Heading: heading, Titulo: $"Resolución de Superintendencia N° {doc}",
        Tipo: TipoNorma.ResolucionSuperintendencia, Numero: doc, Sumilla: "Sumilla de prueba",
        FechaPublicacion: fecha, FechaVigencia: null, Url: new Uri($"https://example.test/{doc}.pdf"), Categoria: cat);

    private static List<DocumentChunk> Corpus() =>
    [
        Chunk("superin-2025-000001-2025", 0, "El plazo para presentar la declaración jurada anual del impuesto a la renta vence en marzo.", "Artículo 1.- Plazo", fecha: new DateOnly(2025, 1, 10)),
        Chunk("superin-2025-000001-2025", 1, "Los sujetos obligados deberán emitir comprobantes de pago electrónicos.", "Artículo 2.- Comprobantes", fecha: new DateOnly(2025, 1, 10)),
        Chunk("superin-2025-000002-2025", 0, "Se designa al responsable del equipo de planeamiento institucional.", null, Categoria.Administrativa, new DateOnly(2025, 3, 5)),
        Chunk("superin-2025-000003-2025", 0, "Resolución sobre el régimen de retenciones del IGV.", "Artículo 1.- Objeto"),
    ];

    private static IndexRequest All => new();

    public void Dispose()
    {
        _sp.Dispose();
        SqliteConnection.ClearAllPools(); // release the file lock on Windows before deleting
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* best effort */ }
    }

    [Fact]
    public async Task First_run_embeds_everything_and_fills_both_indexes()
    {
        var summary = await Indexer.IndexAsync(Corpus(), All, null, CancellationToken.None);

        Assert.Equal(4, summary.ChunksEmbedded);
        Assert.Equal(0, summary.ChunksSkipped);
        Assert.Equal(2, summary.Batches); // batch size 2
        Assert.Equal("fake-model", summary.EmbeddingModel);
        Assert.Equal(4, await Keywords.CountAsync(CancellationToken.None));
        var stored = await Collection.GetAsync(c => c.Year == 2025, 100).ToListAsync();
        Assert.Equal(4, stored.Count);
    }

    [Fact]
    public async Task Second_run_is_a_noop_because_nothing_changed()
    {
        await Indexer.IndexAsync(Corpus(), All, null, CancellationToken.None);
        var callsAfterFirst = _embedder.Calls;

        var summary = await Indexer.IndexAsync(Corpus(), All, null, CancellationToken.None);

        Assert.Equal(0, summary.ChunksEmbedded);
        Assert.Equal(4, summary.ChunksSkipped);
        Assert.Equal(callsAfterFirst, _embedder.Calls);
    }

    [Fact]
    public async Task Changed_text_re_embeds_only_that_chunk()
    {
        await Indexer.IndexAsync(Corpus(), All, null, CancellationToken.None);
        _embedder.Embedded.Clear();

        var changed = Corpus();
        changed[1] = Chunk("superin-2025-000001-2025", 1, "Los sujetos obligados deberán emitir comprobantes de pago electrónicos y guías.", "Artículo 2.- Comprobantes");
        var summary = await Indexer.IndexAsync(changed, All, null, CancellationToken.None);

        Assert.Equal(1, summary.ChunksEmbedded);
        Assert.Equal(3, summary.ChunksSkipped);
        Assert.Single(_embedder.Embedded);
        Assert.Contains("guías", _embedder.Embedded[0]);
    }

    [Fact]
    public async Task Resume_after_a_partial_run_only_embeds_the_missing_documents()
    {
        await Indexer.IndexAsync(Corpus(), new IndexRequest(DocumentLimit: 1), null, CancellationToken.None);
        Assert.Equal(2, _embedder.Embedded.Count);

        var summary = await Indexer.IndexAsync(Corpus(), All, null, CancellationToken.None);

        Assert.Equal(2, summary.ChunksEmbedded); // the two other documents
        Assert.Equal(2, summary.ChunksSkipped);
    }

    [Fact]
    public async Task Changing_the_embedding_recipe_forces_a_full_re_embed()
    {
        await Indexer.IndexAsync(Corpus(), All, null, CancellationToken.None);

        var other = new ChunkIndexer(Collection, Keywords, _embedder,
            Options.Create(new IndexOptions { DatabasePath = Path.Combine(_dir, "test.db"), EmbeddingBatchSize = 2, EmbeddingRecipe = "v2" }),
            _sp.GetRequiredService<IOptions<AiOptions>>(),
            _sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<ChunkIndexer>>());
        var summary = await other.IndexAsync(Corpus(), All, null, CancellationToken.None);

        Assert.Equal(4, summary.ChunksEmbedded);
    }

    [Fact]
    public async Task Chunks_that_disappear_from_a_document_are_removed_from_both_indexes()
    {
        await Indexer.IndexAsync(Corpus(), All, null, CancellationToken.None);

        var shrunk = Corpus().Where(c => c.ChunkId != "superin-2025-000001-2025#0001").ToList();
        var summary = await Indexer.IndexAsync(shrunk, All, null, CancellationToken.None);

        Assert.Equal(1, summary.ChunksDeleted);
        Assert.Null(await Collection.GetAsync("superin-2025-000001-2025#0001"));
        Assert.Equal(3, await Keywords.CountAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Prune_removes_documents_that_left_the_chunk_set()
    {
        await Indexer.IndexAsync(Corpus(), All, null, CancellationToken.None);

        var withoutAdmin = Corpus().Where(c => c.Categoria == Categoria.Normativa).ToList();
        var summary = await Indexer.IndexAsync(withoutAdmin, new IndexRequest(Prune: true), null, CancellationToken.None);

        Assert.Equal(1, summary.ChunksDeleted);
        Assert.Null(await Collection.GetAsync("superin-2025-000002-2025#0000"));
        Assert.DoesNotContain("superin-2025-000002-2025", await Keywords.DocumentIdsAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Vector_search_returns_the_nearest_chunk_with_its_metadata()
    {
        await Indexer.IndexAsync(Corpus(), All, null, CancellationToken.None);
        var query = FakeEmbedder.Vectorize(
            ChunkRecordMapper.EmbeddingText(Corpus()[0]), 8);

        var best = await Collection.SearchAsync(new ReadOnlyMemory<float>(query), 3).FirstAsync();

        Assert.Equal("superin-2025-000001-2025#0000", best.Record.ChunkId);
        Assert.Equal(20250110, best.Record.FechaPublicacionYmd);
        Assert.Equal("Normativa", best.Record.Categoria);
        Assert.StartsWith("Resolución de Superintendencia N°", best.Record.Cita());
    }

    [Fact]
    public async Task Vector_search_filters_by_categoria_and_date()
    {
        await Indexer.IndexAsync(Corpus(), All, null, CancellationToken.None);
        var query = new ReadOnlyMemory<float>(FakeEmbedder.Vectorize("plantilla planeamiento designa responsable", 8));

        var onlyNormativa = await Collection.SearchAsync(query, 10,
            new VectorSearchOptions<ChunkRecord> { Filter = r => r.Categoria == "Normativa" }).ToListAsync();
        var fromFebruary = await Collection.SearchAsync(query, 10,
            new VectorSearchOptions<ChunkRecord> { Filter = r => r.FechaPublicacionYmd >= 20250201 }).ToListAsync();

        Assert.Equal(3, onlyNormativa.Count);
        Assert.DoesNotContain(onlyNormativa, h => h.Record.Categoria == "Administrativa");
        Assert.Single(fromFebruary);
        Assert.Equal("superin-2025-000002-2025#0000", fromFebruary[0].Record.ChunkId);
    }

    [Fact]
    public async Task Keyword_search_is_accent_and_case_insensitive()
    {
        await Indexer.IndexAsync(Corpus(), All, null, CancellationToken.None);

        var hits = await Keywords.SearchAsync("DECLARACION anual renta", 5, CancellationToken.None).ToListAsync();

        Assert.NotEmpty(hits);
        Assert.Equal("superin-2025-000001-2025#0000", hits[0].ChunkId);
        Assert.True(hits[0].Score > 0);
    }

    [Theory]
    [InlineData("la declaración de renta", "\"declaración\" OR \"renta\"")]
    [InlineData("artículo 5", "\"artículo\" OR \"5\"")]
    [InlineData("NEAR(a b) OR \"x\" -foo", "\"near\" OR \"or\" OR \"foo\"")] // operators cannot be smuggled in: tokens are quoted
    public void Match_query_quotes_tokens_and_drops_stopwords(string input, string expected) =>
        Assert.Equal(expected, Fts5KeywordIndex.BuildMatchQuery(input));

    [Theory]
    [InlineData("")]
    [InlineData("de la el")]
    [InlineData("?!¿")]
    public void Match_query_is_null_when_nothing_searchable(string input) =>
        Assert.Null(Fts5KeywordIndex.BuildMatchQuery(input));

    [Fact]
    public async Task Dimension_mismatch_fails_with_an_actionable_message()
    {
        var wrong = new FakeEmbedder(dimensions: 4);
        var indexer = new ChunkIndexer(Collection, Keywords, wrong,
            _sp.GetRequiredService<IOptions<IndexOptions>>(), _sp.GetRequiredService<IOptions<AiOptions>>(),
            _sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<ChunkIndexer>>());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => indexer.IndexAsync(Corpus(), All, null, CancellationToken.None));

        Assert.Contains("EmbeddingDimensions", ex.Message);
        Assert.Contains("--rebuild", ex.Message);
    }

    [Fact]
    public async Task Rebuild_drops_and_recreates_both_indexes()
    {
        await Indexer.IndexAsync(Corpus(), All, null, CancellationToken.None);

        var summary = await Indexer.IndexAsync(Corpus(), new IndexRequest(Rebuild: true), null, CancellationToken.None);

        Assert.Equal(4, summary.ChunksEmbedded);
        Assert.Equal(0, summary.ChunksSkipped);
        Assert.Equal(4, await Keywords.CountAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData("superin-2025-000401-2025", 2025)]
    [InlineData("oficios-2024-i0000131-2024-sunat-7t0000", 2024)]
    [InlineData("weird", 0)]
    public void Year_is_derived_from_the_document_id(string id, int year) =>
        Assert.Equal(year, ChunkRecordMapper.YearOf(id));

    [Fact]
    public void Batcher_respects_both_item_and_token_limits()
    {
        var items = Enumerable.Range(1, 7).Select(_ => 100).ToList();

        var byCount = EmbeddingBatcher.Batch(items, maxItems: 3, maxTokens: 10_000, x => x).Select(b => b.Count).ToList();
        var byTokens = EmbeddingBatcher.Batch(items, maxItems: 100, maxTokens: 250, x => x).Select(b => b.Count).ToList();
        var oversized = EmbeddingBatcher.Batch(new[] { 900, 10 }, maxItems: 10, maxTokens: 500, x => x).Select(b => b.Count).ToList();

        Assert.Equal([3, 3, 1], byCount);
        Assert.Equal([2, 2, 2, 1], byTokens);
        Assert.Equal([1, 1], oversized); // an over-budget item still goes out, alone
    }
}
