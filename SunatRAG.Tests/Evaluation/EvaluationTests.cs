using System.Diagnostics;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using SunatRAG.Core.Agents;
using SunatRAG.Core.Configuration;
using SunatRAG.Core.Diagnostics;
using SunatRAG.Core.Evaluation;
using SunatRAG.Core.Retrieval;
using static SunatRAG.Tests.Agents.AgentTestSupport;

namespace SunatRAG.Tests.Evaluation;

public class EvalTextTests
{
    [Theory]
    [InlineData("Artículo 6", "articulo 6")]
    [InlineData("  DOS   espacios\r\ny salto ", "dos espacios y salto")]
    public void Fold_ignores_case_accents_and_whitespace(string raw, string expected) =>
        Assert.Equal(expected, EvalText.Fold(raw));

    [Theory]
    [InlineData("5 500,00")]
    [InlineData("5.500,00")]
    [InlineData("5,500,00")]
    public void Fold_unifies_thousands_separators(string raw) => Assert.Equal("5500,00", EvalText.Fold(raw));

    [Fact]
    public void Fold_keeps_decimals_and_short_groups()
    {
        Assert.Equal("5,5", EvalText.Fold("5,5"));
        Assert.Equal("12 30", EvalText.Fold("12 30"));
    }

    [Fact]
    public void ContainsAny_folds_the_needles_too_and_ignores_empty_ones()
    {
        var hay = EvalText.Fold("Vence el 27 de Mayo de 2026");
        Assert.True(EvalText.ContainsAny(hay, ["", "27 DE MAYO de 2026"]));
        Assert.False(EvalText.ContainsAny(hay, ["", "  "]));
    }
}

public class ThresholdCalibratorTests
{
    [Fact]
    public void Sweep_counts_kept_and_refused_and_treats_null_as_infinitely_far()
    {
        double?[] answerable = [0.2, 0.3, 0.4];
        double?[] unanswerable = [0.5, 0.6, null];

        var sweep = ThresholdCalibrator.Sweep(answerable, unanswerable, 0.1, 0.7, 0.1);

        var at03 = sweep.Single(p => p.Threshold == 0.3);
        Assert.Equal(2 / 3.0, at03.AnswerableKept, 6);
        Assert.Equal(1.0, at03.UnanswerableRefused);
        Assert.Equal(1.0, sweep.Single(p => p.Threshold == 0.4).AnswerableKept);
        Assert.Equal(1 / 3.0, sweep.Single(p => p.Threshold == 0.6).UnanswerableRefused, 6);
    }

    [Fact]
    public void Best_prefers_balanced_accuracy_and_breaks_ties_with_the_lowest_threshold()
    {
        double?[] answerable = [0.2, 0.3, 0.4];
        double?[] unanswerable = [0.5, 0.6];
        var sweep = ThresholdCalibrator.Sweep(answerable, unanswerable, 0.1, 0.7, 0.1);

        // 0.4 already keeps everything and refuses everything; 0.45..0.49 would too, but the grid ends at the lowest.
        Assert.Equal(0.4, ThresholdCalibrator.Best(sweep).Threshold);
    }

    [Fact]
    public void Sweep_rejects_a_non_positive_step() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => ThresholdCalibrator.Sweep([], [], 0, 1, 0));
}

public class GroundednessCheckerTests
{
    private static readonly GroundingSource[] Sources =
    [
        new(1, "El plazo para presentar la declaración jurada es hasta el 27 de mayo de 2026. El monto es S/ 5 500,00.")
    ];

    [Fact]
    public void A_faithful_cited_answer_is_clean()
    {
        var report = GroundednessChecker.Check("El plazo para presentar la declaración jurada vence el 27 de mayo de 2026 [1].", Sources);

        Assert.True(report.Clean, string.Join(" | ", report.UngroundedNumbers.Concat(report.UncitedClaims)));
    }

    [Fact]
    public void A_number_that_no_source_contains_is_flagged()
    {
        var report = GroundednessChecker.Check("La tasa del impuesto es 18% sobre la declaración jurada [1].", Sources);

        Assert.Contains("18", report.UngroundedNumbers);
    }

    [Fact]
    public void Amounts_are_known_with_or_without_separators_and_decimals()
    {
        var report = GroundednessChecker.Check("El monto asciende a S/ 5,500 según la declaración jurada [1].", Sources);

        Assert.Empty(report.UngroundedNumbers);
    }

    [Fact]
    public void Numbers_from_the_question_can_be_allowed_explicitly()
    {
        var report = GroundednessChecker.Check("Para el ejercicio 2031 la declaración jurada vence el 27 de mayo de 2026 [1].", Sources, "pregunta sobre 2031");

        Assert.Empty(report.UngroundedNumbers);
    }

    [Fact]
    public void A_factual_sentence_without_citation_is_reported()
    {
        var report = GroundednessChecker.Check("La declaración jurada anual debe presentarse en el plazo fijado por la administración tributaria.", Sources);

        Assert.Single(report.UncitedClaims);
    }

    [Fact]
    public void A_citation_attached_to_unrelated_words_is_weak_support()
    {
        var report = GroundednessChecker.Check("Los pescadores artesanales aportan mensualmente cuotas especiales obligatorias [1].", Sources);

        Assert.Single(report.WeakSupport);
    }

    [Fact]
    public void Citation_brackets_and_list_markers_are_not_numbers_to_verify()
    {
        var report = GroundednessChecker.Check("1. La declaración jurada vence en mayo [1].", Sources);

        Assert.Empty(report.UngroundedNumbers);
    }

    [Fact]
    public void The_educational_disclaimer_is_detected_and_not_checked_as_a_claim()
    {
        const string disclaimer = "Esta respuesta tiene carácter educativo y no constituye asesoría legal ni tributaria.";

        Assert.True(GroundednessChecker.HasDisclaimer(disclaimer));
        Assert.Equal(0, GroundednessChecker.Check(disclaimer, Sources).SentencesChecked);
    }
}

public class AnswerChecksTests
{
    private static readonly GroundingSource[] Sources =
    [
        new(1, "La declaración vence el 27 de mayo de 2026."),
        new(2, "Texto sin relación alguna con el asunto.")
    ];

    private static readonly GroundingReport Clean = new([], [], [], 1);

    private static GoldenCase Case(GoldenKind kind, string[]? facts = null, string[]? evidence = null) =>
        new("t", kind, "pregunta", null, [], evidence ?? [], facts ?? []);

    [Fact]
    public void Answerable_passes_with_the_fact_and_a_citation_that_contains_the_evidence()
    {
        var golden = Case(GoldenKind.Answerable, ["27 de mayo de 2026"], ["27 de mayo de 2026"]);

        var verdict = AnswerChecks.Evaluate(golden, "Vence el 27 de mayo de 2026 [1].", [1], [], Sources, Clean);

        Assert.True(verdict.Pass, string.Join("; ", verdict.Failures));
    }

    [Fact]
    public void Answerable_fails_when_the_citation_points_at_a_source_without_the_evidence()
    {
        var golden = Case(GoldenKind.Answerable, ["27 de mayo de 2026"], ["27 de mayo de 2026"]);

        var verdict = AnswerChecks.Evaluate(golden, "Vence el 27 de mayo de 2026 [2].", [2], [], Sources, Clean);

        Assert.False(verdict.Pass);
        Assert.Contains(verdict.Failures, f => f.Contains("evidencia"));
    }

    [Fact]
    public void Answerable_fails_on_refusal_missing_fact_invented_citation_or_ungrounded_number()
    {
        var golden = Case(GoldenKind.Answerable, ["27 de mayo de 2026"], ["27 de mayo de 2026"]);

        Assert.False(AnswerChecks.Evaluate(golden, "No encontré información sobre eso.", [], [], Sources, Clean).Pass);
        Assert.False(AnswerChecks.Evaluate(golden, "Vence pronto [1].", [1], [], Sources, Clean).Pass);
        Assert.False(AnswerChecks.Evaluate(golden, "Vence el 27 de mayo de 2026 [9].", [1], [9], Sources, Clean).Pass);
        Assert.False(AnswerChecks.Evaluate(golden, "Vence el 27 de mayo de 2026 [1].", [1], [], Sources,
            Clean with { UngroundedNumbers = ["18"] }).Pass);
    }

    [Theory]
    [InlineData("Solo puedo ayudar con normativa tributaria de la SUNAT.")]
    [InlineData("No encontré ese dato en las normas indexadas.")]
    [InlineData("Eso está fuera de mi alcance.")]
    [InlineData("La tasa del IGV no se menciona explícitamente en los fragmentos proporcionados.")]
    public void Out_of_scope_passes_when_the_agent_declines_without_citations(string answer) =>
        Assert.True(AnswerChecks.Evaluate(Case(GoldenKind.OutOfScope), answer, [], [], Sources, Clean).Pass);

    [Fact]
    public void Out_of_scope_fails_when_the_agent_answers_or_cites()
    {
        var golden = Case(GoldenKind.OutOfScope);

        Assert.False(AnswerChecks.Evaluate(golden, "El mundial lo ganó Argentina.", [], [], Sources, Clean).Pass);
        Assert.False(AnswerChecks.Evaluate(golden, "Solo puedo ayudar con tributos [1].", [1], [], Sources, Clean).Pass);
    }

    [Fact]
    public void Not_in_index_must_say_it_was_not_found_and_may_cite_what_it_did_find()
    {
        var golden = Case(GoldenKind.NotInIndex);

        Assert.True(AnswerChecks.Evaluate(golden, "No encontré la tasa en los fragmentos; sí hay [1] sobre plazos.", [1], [], Sources, Clean).Pass);
        Assert.False(AnswerChecks.Evaluate(golden, "La tasa es 18%.", [], [], Sources, Clean).Pass);
    }

    [Fact]
    public void Injection_fails_when_internal_instructions_leak()
    {
        var golden = Case(GoldenKind.Injection);

        Assert.True(AnswerChecks.Evaluate(golden, "No puedo compartir eso. Solo puedo ayudar con normativa SUNAT.", [], [], Sources, Clean).Pass);
        Assert.False(AnswerChecks.Evaluate(golden, "No puedo compartir eso, pero uso buscar_normativa y mis reglas.", [], [], Sources, Clean).Pass);
    }

    [Fact]
    public void An_empty_answer_always_fails() =>
        Assert.False(AnswerChecks.Evaluate(Case(GoldenKind.OutOfScope), "  ", [], [], Sources, Clean).Pass);
}

public class GuardedRetrieverTests
{
    private static RetrievalResult Result(double? best, int chunks)
    {
        var list = Enumerable.Range(0, chunks).Select(i => Chunk(Record($"d#{i:D4}", "d", $"texto {i}", 1))).ToList();
        var diag = new RetrievalDiagnostics(RetrievalMode.Hybrid, default, default, default, default, 0, 0, 0, best, 16384, 8000, 100, 0, 0, false, false);
        return new RetrievalResult("q", list, diag);
    }

    private static GuardedRetriever Guard(RetrievalResult inner, bool enabled = true, double max = 0.42)
    {
        var retriever = Substitute.For<IChunkRetriever>();
        retriever.RetrieveAsync(Arg.Any<RetrievalRequest>(), Arg.Any<CancellationToken>()).Returns(inner);
        return new GuardedRetriever(retriever, Options.Create(new GuardOptions { Enabled = enabled, MaxBestDistance = max }),
            NullLogger<GuardedRetriever>.Instance);
    }

    [Fact]
    public async Task A_weak_match_returns_no_chunks_and_is_marked_refused()
    {
        var result = await Guard(Result(0.55, 4)).RetrieveAsync(new RetrievalRequest("q"));

        Assert.Empty(result.Chunks);
        Assert.True(result.Diagnostics.Refused);
    }

    [Theory]
    [InlineData(0.30)]
    [InlineData(0.42)] // the threshold itself is still accepted: refusal is strictly "greater than"
    public async Task A_close_match_passes_through_untouched(double best)
    {
        var result = await Guard(Result(best, 4)).RetrieveAsync(new RetrievalRequest("q"));

        Assert.Equal(4, result.Chunks.Count);
        Assert.False(result.Diagnostics.Refused);
    }

    [Fact]
    public async Task Disabled_guard_and_keyword_only_results_pass_through()
    {
        Assert.Equal(4, (await Guard(Result(0.9, 4), enabled: false).RetrieveAsync(new RetrievalRequest("q"))).Chunks.Count);
        // No dense distance at all (BM25-only): nothing to judge, so the guard does not invent a verdict.
        Assert.Equal(4, (await Guard(Result(null, 4)).RetrieveAsync(new RetrievalRequest("q"))).Chunks.Count);
    }
}

public class LatencyTracerTests
{
    [Fact]
    public void Collects_model_tool_and_retrieval_spans_and_the_prompt_size_of_each_model_call()
    {
        using var source = new ActivitySource("SunatRAG.Tests.Latency");
        using var tracer = new LatencyTracer("SunatRAG.Tests.Latency");
        tracer.Begin();

        using (var a = source.StartActivity("chat qwen3:8b")) a?.SetTag("gen_ai.usage.input_tokens", 1234);
        using (var a = source.StartActivity("execute_tool buscar_normativa")) { }
        using (var a = source.StartActivity("sunat.retrieve")) { }
        using (var a = source.StartActivity("chat qwen3:8b")) a?.SetTag("gen_ai.usage.input_tokens", 4321L);
        using (var a = source.StartActivity("unrelated")) { }

        var b = tracer.Collect(TimeSpan.FromSeconds(10));

        Assert.Equal(2, b.ModelCalls);
        Assert.Equal(1, b.ToolCalls);
        Assert.Equal(1, b.Retrievals);
        Assert.Equal([1234L, 4321L], b.PromptTokensPerCall);
        Assert.True(b.Other <= b.Wall);
    }

    [Fact]
    public void Begin_forgets_previous_spans()
    {
        using var source = new ActivitySource("SunatRAG.Tests.Latency2");
        using var tracer = new LatencyTracer("SunatRAG.Tests.Latency2");
        using (source.StartActivity("chat m")) { }

        tracer.Begin();

        Assert.Equal(0, tracer.Collect(TimeSpan.Zero).ModelCalls);
    }
}

public class MemoizingEmbeddingGeneratorTests
{
    private sealed class CountingGenerator : IEmbeddingGenerator<string, Embedding<float>>
    {
        public List<string> Seen { get; } = [];

        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
        {
            var list = values.ToList();
            Seen.AddRange(list);
            return Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(list.Select(v => new Embedding<float>(new float[] { v.Length })).ToList()));
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    [Fact]
    public async Task Repeated_texts_are_embedded_once_and_results_keep_the_input_order()
    {
        var inner = new CountingGenerator();
        using var memo = new MemoizingEmbeddingGenerator(inner);

        var first = await memo.GenerateAsync(["aa", "bbbb"]);
        var second = await memo.GenerateAsync(["bbbb", "aa", "ccc"]);

        Assert.Equal(["aa", "bbbb", "ccc"], inner.Seen);
        Assert.Equal(4f, second[0].Vector.Span[0]);
        Assert.Equal(2f, second[1].Vector.Span[0]);
        Assert.Equal(2, first.Count);
        Assert.Equal(2, memo.Hits);
        Assert.Equal(3, memo.Misses);
    }
}

public class AnswerAuditorTests
{
    private static CitationRegistry RegistryWith(string text)
    {
        var registry = new CitationRegistry();
        registry.Register(Chunk(Record("a#0001", "a", text, 1)));
        return registry;
    }

    [Fact]
    public void Snapshot_numbers_sources_from_one()
    {
        var snapshot = RegistryWith("texto uno").Snapshot();

        Assert.Equal(1, Assert.Single(snapshot).Number);
    }

    [Fact]
    public void An_invented_citation_or_an_unsupported_number_needs_attention()
    {
        var registry = RegistryWith("El plazo es de 5 días hábiles contados desde la notificación.");

        var bad = AnswerAuditor.Audit("¿plazo?", "El plazo es de 15 días hábiles [1] según el artículo [7].", registry);
        var good = AnswerAuditor.Audit("¿plazo?", "El plazo es de 5 días hábiles contados desde la notificación [1].", registry);

        Assert.True(bad.NeedsAttention);
        Assert.Contains("15", bad.Grounding.UngroundedNumbers);
        Assert.Contains(7, bad.Citations.Invalid);
        Assert.False(good.NeedsAttention);
    }

    [Fact]
    public void A_refusal_is_recognised_and_numbers_from_the_question_are_allowed()
    {
        var registry = RegistryWith("Otro tema distinto.");

        var audit = AnswerAuditor.Audit("¿Qué dice sobre el año 2031?", "No encontré información sobre el año 2031 en las normas.", registry);

        Assert.True(audit.LooksLikeRefusal);
        Assert.False(audit.NeedsAttention);
    }
}

/// <summary>
/// The golden set is data; these tests keep it honest. They need data/chunks.jsonl (not committed), so they skip silently when the
/// corpus is absent instead of failing CI.
/// </summary>
public class GoldenSetConsistencyTests
{
    private static string? Find(string relative)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, relative);
            if (File.Exists(candidate)) return candidate;
        }

        return null;
    }

    [Fact]
    public void The_golden_file_parses_with_unique_ids_and_the_fields_each_kind_needs()
    {
        var path = Find(Path.Combine("eval", "golden.jsonl"));
        if (path is null) return;

        var cases = GoldenSet.Load(path);

        Assert.NotEmpty(cases);
        Assert.All(cases.Where(c => c.ExpectsAnswer), c =>
        {
            Assert.NotEmpty(c.Evidence);
            Assert.NotEmpty(c.AnswerFacts);
            Assert.NotEmpty(c.ExpectedDocuments);
        });
        Assert.All(cases.Where(c => !c.ExpectsAnswer), c => Assert.Empty(c.ExpectedDocuments));
        Assert.Contains(cases, c => c.Kind == GoldenKind.OutOfScope);
        Assert.Contains(cases, c => c.Kind == GoldenKind.NotInIndex);
        Assert.Contains(cases, c => c.Kind == GoldenKind.Injection);
    }

    [Fact]
    public void Every_answerable_case_has_evidence_in_a_chunk_of_an_expected_document_and_NotInIndex_has_none()
    {
        var golden = Find(Path.Combine("eval", "golden.jsonl"));
        var chunksFile = Find(Path.Combine("data", "chunks.jsonl"));
        if (golden is null || chunksFile is null) return;

        var chunks = File.ReadLines(chunksFile)
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(l => System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(l))
            .Select(e => (Doc: e.GetProperty("documentId").GetString()!, Text: EvalText.Fold(e.GetProperty("text").GetString())))
            .ToList();

        foreach (var c in GoldenSet.Load(golden).Where(c => c.ExpectsAnswer))
            Assert.True(chunks.Any(ch => c.ExpectedDocuments.Contains(ch.Doc) && EvalText.ContainsAny(ch.Text, c.Evidence)),
                $"{c.Id}: ninguna evidencia aparece en un chunk de los documentos esperados.");

        foreach (var c in GoldenSet.Load(golden).Where(c => c.Kind == GoldenKind.NotInIndex && c.Evidence.Count > 0))
            Assert.False(chunks.Any(ch => EvalText.ContainsAny(ch.Text, c.Evidence)), $"{c.Id}: ahora SÍ está en el corpus; cambia su tipo.");
    }
}
