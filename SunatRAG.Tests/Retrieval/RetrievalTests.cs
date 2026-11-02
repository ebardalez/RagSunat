using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.VectorData;
using SunatRAG.Core.Composition;
using SunatRAG.Core.Configuration;
using SunatRAG.Core.Documents;
using SunatRAG.Core.Indexing;
using SunatRAG.Core.Retrieval;
using SunatRAG.Tests.Indexing;

namespace SunatRAG.Tests.Retrieval;

public class RankFusionTests
{
    [Fact]
    public void A_chunk_both_rankers_like_beats_one_that_only_one_ranker_loves()
    {
        // vector: a, b, c   keyword: c, b, d   (k = 60)
        var fused = RankFusion.ReciprocalRank(["a", "b", "c"], ["c", "b", "d"]);

        // c = 1/63 + 1/61, b = 1/62 + 1/62, a = 1/61, d = 1/63
        Assert.Equal(["c", "b", "a", "d"], fused.Select(f => f.Key));
        Assert.Equal(1 / 63.0 + 1 / 61.0, fused[0].Score, precision: 10);
    }

    [Fact]
    public void Provenance_records_the_rank_in_each_list()
    {
        var fused = RankFusion.ReciprocalRank(["a", "b", "c"], ["c", "b", "d"]);

        var c = fused.Single(f => f.Key == "c");
        var a = fused.Single(f => f.Key == "a");
        Assert.Equal(3, c.VectorRank);
        Assert.Equal(1, c.KeywordRank);
        Assert.Equal(1, a.VectorRank);
        Assert.Null(a.KeywordRank);
    }

    [Fact]
    public void A_zero_weight_silences_a_ranker_but_keeps_its_items()
    {
        var fused = RankFusion.ReciprocalRank(["a", "b", "c"], ["c", "b", "d"], keywordWeight: 0);

        Assert.Equal(["a", "b", "c", "d"], fused.Select(f => f.Key));
    }

    [Fact]
    public void Duplicates_inside_one_list_count_once()
    {
        var fused = RankFusion.ReciprocalRank(["a", "a"], []);

        Assert.Single(fused);
        Assert.Equal(1 / 61.0, fused[0].Score, precision: 10);
    }

    [Fact]
    public void Ties_break_deterministically_by_key()
    {
        var one = RankFusion.ReciprocalRank(["x"], ["y"]);
        var two = RankFusion.ReciprocalRank(["x"], ["y"]);

        Assert.Equal(["x", "y"], one.Select(f => f.Key));
        Assert.Equal(one.Select(f => f.Key), two.Select(f => f.Key));
    }
}

public class ContextBudgetTests
{
    private sealed record Item(string Doc, int Cost);

    [Fact]
    public void Budget_is_the_window_minus_every_reservation()
    {
        // 16384 - 1500 - 2000 - 4096 - 100
        Assert.Equal(8688, ContextBudget.Available(16384, new RetrievalOptions(), questionTokens: 100));
        Assert.Equal(0, ContextBudget.Available(2000, new RetrievalOptions(), questionTokens: 100)); // never negative
    }

    [Fact]
    public void Chunk_cost_applies_the_safety_factor_and_the_header_overhead()
    {
        // ceil(400 * 1.25) + 40
        Assert.Equal(540, ContextBudget.ChunkCost(400, new RetrievalOptions()));
    }

    [Fact]
    public void Selector_applies_the_document_cap_and_the_budget_but_keeps_walking_after_a_skip()
    {
        Item[] ranked = [new("a", 100), new("a", 100), new("a", 100), new("b", 500), new("c", 100), new("d", 100)];

        var s = ContextSelector.Select(ranked, topK: 4, maxPerDocument: 2, budgetTokens: 450, x => x.Doc, x => x.Cost);

        Assert.Equal(["a", "a", "c", "d"], s.Selected.Select(x => x.Doc));
        Assert.Equal(400, s.UsedTokens);
        Assert.Equal(1, s.SkippedForDocumentCap); // third "a"
        Assert.Equal(1, s.SkippedForBudget);      // "b" costs 500 > 250 left
    }

    [Fact]
    public void Selector_stops_at_top_k()
    {
        Item[] ranked = [new("a", 1), new("b", 1), new("c", 1)];

        var s = ContextSelector.Select(ranked, topK: 2, maxPerDocument: 9, budgetTokens: 100, x => x.Doc, x => x.Cost);

        Assert.Equal(2, s.Selected.Count);
    }
}

public class ProyectoDetectorTests
{
    [Theory]
    [InlineData("Disponen la publicación del proyecto de resolución de superintendencia que modifica la Resolución de Superintendencia N° 1")]
    [InlineData("Dispone la publicación en la Sede Digital de la SUNAT del Proyecto de Resolución de Superintendencia que aprueba")]
    [InlineData("Dispone publicación de Proyecto de Resolución de Superintendencia que modifica los procedimientos generales")]
    [InlineData("De dispone la publicación del Proyecto de Resolución de Superintendencia que modifica")]
    [InlineData("Se dispone la publicación del Proyecto de Resolución de Superintendencia que incorpora")]
    public void Real_draft_sumillas_are_detected(string sumilla) => Assert.True(ProyectoDetector.IsProyecto(sumilla));

    [Theory]
    [InlineData("Aprueban el procedimiento específico \"Fiscalización\" y modifica la Resolución de Superintendencia")]
    [InlineData("Con relación a los beneficios tributarios, el proyecto de ley no es aplicable")]
    [InlineData("")]
    [InlineData(null)]
    public void Ordinary_sumillas_are_not_drafts(string? sumilla) => Assert.False(ProyectoDetector.IsProyecto(sumilla));

    [Fact]
    public void An_informe_is_never_a_draft_even_if_its_sumilla_says_so()
    {
        var informe = new ChunkRecord { Tipo = nameof(TipoNorma.Informe), Sumilla = "Dispone la publicación del Proyecto de Resolución" };
        var resolucion = new ChunkRecord { Tipo = nameof(TipoNorma.ResolucionSuperintendencia), Sumilla = informe.Sumilla };

        Assert.False(ProyectoDetector.IsProyecto(informe));
        Assert.True(ProyectoDetector.IsProyecto(resolucion));
    }
}

public class KeywordStopwordTests
{
    [Theory]
    [InlineData("¿Cuál es el plazo?", "\"plazo\"")]
    [InlineData("¿Qué plazo está vigente?", "\"plazo\" OR \"vigente\"")]
    public void Accented_stopwords_are_dropped(string input, string expected) =>
        Assert.Equal(expected, Fts5KeywordIndex.BuildMatchQuery(input));
}

public class LlmRerankerTests
{
    private sealed class StubChat(string reply) : IChatClient
    {
        public int Calls { get; private set; }
        public string? LastUserPrompt { get; private set; }
        public ChatOptions? LastOptions { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            LastOptions = options;
            LastUserPrompt = messages.Last().Text;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, reply)));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private static List<ChunkRecord> Records(int n) => Enumerable.Range(1, n)
        .Select(i => new ChunkRecord { ChunkId = $"c{i}", Titulo = $"Titulo {i}", Heading = $"Artículo {i}", Text = $"Texto {i}" }).ToList();

    private static LlmReranker Reranker(IChatClient chat) =>
        new(chat, Options.Create(new RetrievalOptions()), NullLogger<LlmReranker>.Instance);

    [Fact]
    public void Parse_reads_a_plain_list() =>
        Assert.Equal([2, 0, 1], LlmReranker.ParseOrder("3, 1, 2", 3));

    [Fact]
    public void Parse_ignores_the_digits_inside_a_reasoning_block() =>
        Assert.Equal([1, 0, 2], LlmReranker.ParseOrder("<think>quizá 3 y luego 1</think>\n2, 1", 3));

    [Fact]
    public void Parse_drops_out_of_range_and_repeated_numbers_and_appends_the_forgotten_ones() =>
        Assert.Equal([2, 0, 1], LlmReranker.ParseOrder("3, 3, 9, 1", 3));

    [Theory]
    [InlineData("no sé")]
    [InlineData("<think>pensando 1 2 3 sin cerrar")]
    [InlineData("")]
    public void Parse_returns_null_when_there_is_no_usable_answer(string raw) =>
        Assert.Null(LlmReranker.ParseOrder(raw, 3));

    [Fact]
    public async Task Rerank_returns_the_models_order_and_sends_numbered_candidates()
    {
        var chat = new StubChat("2, 3, 1");

        var result = await Reranker(chat).RerankAsync("¿plazo?", Records(3), CancellationToken.None);

        Assert.False(result.FellBack);
        Assert.Equal([1, 2, 0], result.Order);
        Assert.Contains("[1] Titulo 1 — Artículo 1", chat.LastUserPrompt);
        Assert.Contains("[3] Titulo 3", chat.LastUserPrompt);
        Assert.Equal(ReasoningEffort.None, chat.LastOptions?.Reasoning?.Effort); // thinking must be off for a reranker
    }

    [Fact]
    public async Task Rerank_falls_back_to_the_original_order_on_garbage()
    {
        var result = await Reranker(new StubChat("lo siento, no puedo")).RerankAsync("q", Records(3), CancellationToken.None);

        Assert.True(result.FellBack);
        Assert.Equal([0, 1, 2], result.Order);
    }

    [Fact]
    public async Task Rerank_skips_the_model_call_when_there_is_nothing_to_reorder()
    {
        var chat = new StubChat("1");

        var result = await Reranker(chat).RerankAsync("q", Records(1), CancellationToken.None);

        Assert.Equal(0, chat.Calls);
        Assert.Equal([0], result.Order);
    }
}

/// <summary>End to end against REAL sqlite-vec + FTS5 (temp file) with a deterministic fake embedder: proves filters, fusion and budgeting without Ollama.</summary>
public sealed class HybridRetrieverTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sunatrag-retrieval-" + Guid.NewGuid().ToString("N"));
    private readonly FakeEmbedder _embedder = new();
    private readonly ServiceProvider _sp;

    public HybridRetrieverTests()
    {
        Directory.CreateDirectory(_dir);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Ai:Ollama:Endpoint"] = "http://ollama.test:11434",
            ["Ai:Ollama:EmbeddingDimensions"] = "8",
            ["Index:DatabasePath"] = Path.Combine(_dir, "test.db"),
        }).Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.Configure<AiOptions>(config.GetSection("Ai"));
        services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(_embedder);
        services.AddSunatIndex(config);
        services.AddOptions<RetrievalOptions>();
        services.AddSingleton<HybridRetriever>(); // no LlmReranker registered: the optional constructor parameter stays null
        _sp = services.BuildServiceProvider();
    }

    public void Dispose()
    {
        _sp.Dispose();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* best effort */ }
    }

    private HybridRetriever Retriever => _sp.GetRequiredService<HybridRetriever>();

    private static DocumentChunk Chunk(string doc, int index, string text, string? heading = null, Categoria cat = Categoria.Normativa,
        DateOnly? fecha = null, TipoNorma tipo = TipoNorma.ResolucionSuperintendencia, string? sumilla = "Sumilla de prueba") => new(
        ChunkId: $"{doc}#{index:D4}", DocumentId: doc, Index: index, Text: text, TokenCount: 50,
        TextHash: Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16],
        PageStart: 1, PageEnd: 1, Heading: heading, Titulo: $"{tipo.DisplayName()} N° {doc}",
        Tipo: tipo, Numero: doc, Sumilla: sumilla, FechaPublicacion: fecha, FechaVigencia: null,
        Url: new Uri($"https://example.test/{doc}.pdf"), Categoria: cat);

    private static List<DocumentChunk> Corpus() =>
    [
        Chunk("superin-2025-000001-2025", 0, "El plazo para presentar la declaración jurada anual del impuesto a la renta vence en marzo.", "Artículo 1.- Plazo", fecha: new DateOnly(2025, 1, 10)),
        Chunk("superin-2025-000001-2025", 1, "Los sujetos obligados deberán emitir comprobantes de pago electrónicos.", "Artículo 2.- Comprobantes", fecha: new DateOnly(2025, 1, 10)),
        Chunk("superin-2025-000002-2025", 0, "Se designa al responsable del equipo de planeamiento institucional.", null, Categoria.Administrativa, new DateOnly(2025, 3, 5)),
        Chunk("superin-2025-000003-2025", 0, "Resolución sobre el régimen de retenciones del IGV.", "Artículo 1.- Objeto"),
        Chunk("superin-2025-000004-2025", 0, "Proyecto: el plazo de la declaración jurada anual se ampliará.", "Artículo 1.- Objeto", fecha: new DateOnly(2025, 2, 1),
            sumilla: "Dispone la publicación del Proyecto de Resolución de Superintendencia que modifica el plazo de la declaración jurada"),
        Chunk("oficios-2025-informe-i000005-2025", 0, "El informe analiza las retenciones del IGV a cargo de agentes.", null, fecha: new DateOnly(2025, 4, 2), tipo: TipoNorma.Informe)
    ];

    private async Task IndexAsync() =>
        await _sp.GetRequiredService<ChunkIndexer>().IndexAsync(Corpus(), new IndexRequest(), null, CancellationToken.None);

    private static IEnumerable<string> Docs(RetrievalResult r) => r.Chunks.Select(c => c.Record.DocumentId);

    [Fact]
    public async Task A_chunk_found_by_both_rankers_comes_first_and_carries_both_ranks()
    {
        await IndexAsync();

        var result = await Retriever.RetrieveAsync(new RetrievalRequest("plazo declaración jurada anual renta"));

        var top = result.Chunks[0];
        Assert.Equal("superin-2025-000001-2025#0000", top.Record.ChunkId);
        Assert.Equal(1, top.KeywordRank);
        Assert.NotNull(top.VectorRank);
        Assert.NotNull(top.VectorDistance);
        Assert.NotNull(top.Bm25Score);
        Assert.StartsWith("Resolución de Superintendencia N°", top.Cita);
        Assert.NotNull(result.Diagnostics.BestVectorDistance);
    }

    [Fact]
    public async Task Single_ranker_modes_only_fill_their_own_provenance()
    {
        await IndexAsync();

        var vector = await Retriever.RetrieveAsync(new RetrievalRequest("plazo comprobantes", Mode: RetrievalMode.VectorOnly));
        var keyword = await Retriever.RetrieveAsync(new RetrievalRequest("plazo comprobantes", Mode: RetrievalMode.KeywordOnly));

        Assert.NotEmpty(vector.Chunks);
        Assert.All(vector.Chunks, c => Assert.Null(c.KeywordRank));
        Assert.NotEmpty(keyword.Chunks);
        Assert.All(keyword.Chunks, c => Assert.Null(c.VectorRank));
    }

    [Fact]
    public async Task Administrativas_are_excluded_by_default_and_included_on_request()
    {
        await IndexAsync();
        var request = new RetrievalRequest("designa responsable equipo planeamiento");

        var normal = await Retriever.RetrieveAsync(request);
        var all = await Retriever.RetrieveAsync(request with { Filters = new SearchFilters { Categoria = null } });

        Assert.DoesNotContain("superin-2025-000002-2025", Docs(normal));
        Assert.Contains("superin-2025-000002-2025", Docs(all));
    }

    [Fact]
    public async Task Drafts_are_excluded_by_default_and_included_on_request()
    {
        await IndexAsync();
        var request = new RetrievalRequest("plazo declaración jurada anual");

        var normal = await Retriever.RetrieveAsync(request);
        var withDrafts = await Retriever.RetrieveAsync(request with { Filters = new SearchFilters { IncluirProyectos = true } });

        Assert.DoesNotContain("superin-2025-000004-2025", Docs(normal));
        Assert.Contains("superin-2025-000004-2025", Docs(withDrafts));
    }

    [Fact]
    public async Task Tipo_filter_applies_to_both_rankers()
    {
        await IndexAsync();

        var result = await Retriever.RetrieveAsync(
            new RetrievalRequest("retenciones IGV", new SearchFilters { Tipo = TipoNorma.Informe }));

        Assert.Equal(["oficios-2025-informe-i000005-2025"], Docs(result));
    }

    [Fact]
    public async Task Year_filter_is_pushed_down_to_the_vector_store_and_applied_to_bm25()
    {
        await IndexAsync();

        var in2025 = await Retriever.RetrieveAsync(new RetrievalRequest("retenciones IGV", new SearchFilters { Year = 2025 }));
        var in2024 = await Retriever.RetrieveAsync(new RetrievalRequest("retenciones IGV", new SearchFilters { Year = 2024 }));

        Assert.NotEmpty(in2025.Chunks);
        Assert.Empty(in2024.Chunks);
    }

    [Fact]
    public async Task Date_filter_drops_chunks_published_before_it_and_chunks_without_a_date()
    {
        await IndexAsync();

        var result = await Retriever.RetrieveAsync(
            new RetrievalRequest("retenciones IGV", new SearchFilters { PublicadoDesde = new DateOnly(2025, 2, 1) }));

        Assert.Equal(["oficios-2025-informe-i000005-2025"], Docs(result)); // 000003 has no date: null >= x is false
    }

    [Fact]
    public async Task Combined_filters_are_ANDed()
    {
        await IndexAsync();

        var result = await Retriever.RetrieveAsync(new RetrievalRequest("retenciones IGV plazo comprobantes",
            new SearchFilters { Tipo = TipoNorma.ResolucionSuperintendencia, PublicadoHasta = new DateOnly(2025, 1, 31) }));

        Assert.NotEmpty(result.Chunks);
        Assert.All(result.Chunks, c =>
        {
            Assert.Equal("ResolucionSuperintendencia", c.Record.Tipo);
            Assert.True(c.Record.FechaPublicacionYmd <= 20250131);
        });
    }

    [Fact]
    public async Task The_diversity_cap_limits_chunks_per_document()
    {
        await IndexAsync();
        var capped = new HybridRetriever(
            _sp.GetRequiredService<VectorStoreCollection<string, ChunkRecord>>(), _sp.GetRequiredService<Fts5KeywordIndex>(), _embedder,
            Options.Create(new RetrievalOptions { MaxChunksPerDocument = 1 }), _sp.GetRequiredService<IOptions<AiOptions>>(),
            _sp.GetRequiredService<ILogger<HybridRetriever>>());

        var result = await capped.RetrieveAsync(new RetrievalRequest("plazo comprobantes"));

        Assert.Equal(1, result.Chunks.Count(c => c.Record.DocumentId == "superin-2025-000001-2025")); // it has two matching chunks
        Assert.True(result.Diagnostics.SkippedForDocumentCap >= 1);
    }

    [Fact]
    public async Task The_token_budget_limits_the_context()
    {
        await IndexAsync();
        // window 8000 - 1500 - 2000 - 4096 - question(30 chars = 10 tokens) = 394; each chunk costs ceil(50*1.25)+40 = 103 -> 3 fit
        var tight = new HybridRetriever(
            _sp.GetRequiredService<VectorStoreCollection<string, ChunkRecord>>(), _sp.GetRequiredService<Fts5KeywordIndex>(), _embedder,
            Options.Create(new RetrievalOptions()),
            Options.Create(new AiOptions { Ollama = new OllamaOptions { ContextLength = 8000 } }),
            _sp.GetRequiredService<ILogger<HybridRetriever>>());

        var result = await tight.RetrieveAsync(new RetrievalRequest("plazo comprobantes retenciones"));

        Assert.Equal(394, result.Diagnostics.BudgetTokens);
        Assert.Equal(3, result.Chunks.Count);
        Assert.Equal(309, result.Diagnostics.UsedTokens);
        Assert.Equal(1, result.Diagnostics.SkippedForBudget);
    }

    [Fact]
    public async Task Rerank_without_a_registered_reranker_fails_loudly()
    {
        await IndexAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Retriever.RetrieveAsync(new RetrievalRequest("plazo", Rerank: RerankMode.Llm)));
    }

    [Fact]
    public async Task Llm_rerank_reorders_the_head_of_the_list()
    {
        await IndexAsync();
        var baseline = await Retriever.RetrieveAsync(new RetrievalRequest("plazo comprobantes retenciones"));
        Assert.Equal(4, baseline.Chunks.Count);

        var reranker = new LlmReranker(new ReverseChat(), Options.Create(new RetrievalOptions()), NullLogger<LlmReranker>.Instance);
        var withRerank = new HybridRetriever(
            _sp.GetRequiredService<VectorStoreCollection<string, ChunkRecord>>(), _sp.GetRequiredService<Fts5KeywordIndex>(), _embedder,
            Options.Create(new RetrievalOptions()), _sp.GetRequiredService<IOptions<AiOptions>>(),
            _sp.GetRequiredService<ILogger<HybridRetriever>>(), reranker);

        var result = await withRerank.RetrieveAsync(new RetrievalRequest("plazo comprobantes retenciones", Rerank: RerankMode.Llm));

        Assert.True(result.Diagnostics.RerankApplied);
        Assert.Equal(baseline.Chunks.Select(c => c.Record.ChunkId).Reverse(), result.Chunks.Select(c => c.Record.ChunkId));
    }

    [Fact]
    public async Task An_empty_query_is_rejected()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Retriever.RetrieveAsync(new RetrievalRequest("   ")));
    }

    [Fact]
    public async Task Context_formatter_numbers_the_sources_with_their_citation()
    {
        await IndexAsync();
        var result = await Retriever.RetrieveAsync(new RetrievalRequest("plazo declaración jurada anual renta", Mode: RetrievalMode.KeywordOnly));

        var block = RetrievalContextFormatter.Format(result.Chunks);

        Assert.StartsWith("[1] Resolución de Superintendencia N° superin-2025-000001-2025, pág. 1 (Artículo 1.- Plazo)", block);
        Assert.Contains("vence en marzo", block);
    }

    /// <summary>Answers "N, N-1, ..., 1" for however many candidates the prompt lists.</summary>
    private sealed class ReverseChat : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var prompt = messages.Last().Text;
            var n = System.Text.RegularExpressions.Regex.Matches(prompt, @"^\[\d+\] ", System.Text.RegularExpressions.RegexOptions.Multiline).Count;
            var reply = string.Join(", ", Enumerable.Range(1, n).Reverse());
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, reply)));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
