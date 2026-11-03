using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.VectorData;
using SunatRAG.Core.Agents;
using SunatRAG.Core.Composition;
using SunatRAG.Core.Configuration;
using SunatRAG.Core.Diagnostics;
using SunatRAG.Core.Evaluation;
using SunatRAG.Core.Indexing;
using SunatRAG.Core.Retrieval;

namespace SunatRAG.Ingestion.Commands;

/// <summary>
/// Phase 6: measure instead of guess.
///   eval retrieval : fast (no chat model). Does the answer-bearing chunk reach the prompt? Raw question vs rewritten query,
///                    hybrid vs vector vs BM25, TopK x per-document-cap sweep, and the distance threshold for the weak-match guard.
///   eval agent     : slow (full agent per question). Pass/fail per golden case, groundedness, latency breakdown, optional LLM judge.
/// </summary>
public static class EvalCommand
{
    public const string Usage =
        "Uso: dotnet run --project SunatRAG.Ingestion -- eval retrieval [--golden eval/golden.jsonl]\n" +
        "       dotnet run --project SunatRAG.Ingestion -- eval agent [--golden eval/golden.jsonl] [--only id1,id2] [--limit N] [--judge] [--out data/eval]";

    private static readonly JsonSerializerOptions JsonLines = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static Task<int> RunAsync(IServiceProvider sp, string[] args, CancellationToken ct)
    {
        Console.OutputEncoding = Encoding.UTF8;
        var sub = args.FirstOrDefault();
        var rest = args.Skip(1).ToArray();
        return sub switch
        {
            "retrieval" => RetrievalAsync(sp, rest, ct),
            "agent" => AgentAsync(sp, rest, ct),
            _ => Task.FromResult(Fail("Falta el subcomando (retrieval | agent)."))
        };
    }

    // ───────────────────────────────────────────────────────────────────────────────────────
    // eval retrieval
    // ───────────────────────────────────────────────────────────────────────────────────────

    private static async Task<int> RetrievalAsync(IServiceProvider sp, string[] args, CancellationToken ct)
    {
        var opts = ParseOptions(args);
        if (opts.Error is not null) return Fail(opts.Error);

        var cases = GoldenSet.Load(opts.GoldenPath);
        var answerable = cases.Where(c => c.ExpectsAnswer).ToList();
        var unanswerable = cases.Where(c => !c.ExpectsAnswer).ToList();
        Console.WriteLine($"Golden set: {cases.Count} casos ({answerable.Count} con respuesta en el índice, {unanswerable.Count} sin ella).");

        var embedder = new MemoizingEmbeddingGenerator(sp.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>());
        HybridRetriever Make(params (string Key, string Value)[] overrides)
        {
            // Same configuration the app uses, plus overrides: a variant is "the app, with TopK=8" rather than a hand-built copy.
            var merged = new ConfigurationBuilder()
                .AddConfiguration(sp.GetRequiredService<IConfiguration>())
                .AddInMemoryCollection(overrides.Select(o => new KeyValuePair<string, string?>($"Retrieval:{o.Key}", o.Value)))
                .Build();
            var retrieval = merged.GetSection(RetrievalOptions.SectionName).Get<RetrievalOptions>() ?? new RetrievalOptions();
            return new HybridRetriever(
                sp.GetRequiredService<VectorStoreCollection<string, ChunkRecord>>(),
                sp.GetRequiredService<Fts5KeywordIndex>(),
                embedder,
                Options.Create(retrieval),
                sp.GetRequiredService<IOptions<AiOptions>>(),
                sp.GetRequiredService<ILogger<HybridRetriever>>());
        }

        async Task<List<CaseRetrieval>> RunSet(HybridRetriever retriever, IEnumerable<GoldenCase> set, Func<GoldenCase, string> query, RetrievalMode mode)
        {
            var runs = new List<CaseRetrieval>();
            foreach (var c in set)
            {
                var q = query(c);
                var result = await retriever.RetrieveAsync(new RetrievalRequest(q, new SearchFilters(), null, mode), ct);
                runs.Add(RetrievalMetrics.Score(c, q, result));
            }

            return runs;
        }

        string Raw(GoldenCase c) => c.Question;
        string Rewritten(GoldenCase c) => c.RewrittenOrRaw;

        var baseline = Make();
        var baseOptions = sp.GetRequiredService<IOptions<RetrievalOptions>>().Value;
        Console.WriteLine($"Configuración de la app: TopK {baseOptions.TopK}, tope por documento {baseOptions.MaxChunksPerDocument}, " +
                          $"candidatos por ranker {baseOptions.CandidatesPerRanker}, RRF k {baseOptions.RrfK}.");

        // ── 1. Ranker and query form ─────────────────────────────────────────────────────
        Console.WriteLine("\n══ 1. ¿Qué aporta cada ranker y reescribir la consulta? (solo casos con respuesta) ══");
        Console.WriteLine("   'evidencia' = el fragmento que contiene la respuesta llegó al prompt. 'documento' = llegó algún fragmento del documento correcto.");
        PrintHeader();
        List<CaseRetrieval>? hybridRaw = null, hybridRewrite = null;
        foreach (var (mode, label) in new[] { (RetrievalMode.Hybrid, "híbrido"), (RetrievalMode.VectorOnly, "solo vectorial"), (RetrievalMode.KeywordOnly, "solo BM25") })
        {
            var raw = await RunSet(baseline, answerable, Raw, mode);
            var rewritten = await RunSet(baseline, answerable, Rewritten, mode);
            if (mode == RetrievalMode.Hybrid) (hybridRaw, hybridRewrite) = (raw, rewritten);
            PrintRow(RetrievalMetrics.Summarize($"{label} · pregunta original", raw));
            PrintRow(RetrievalMetrics.Summarize($"{label} · consulta reescrita", rewritten));
        }

        // ── 2. TopK x cap ────────────────────────────────────────────────────────────────
        Console.WriteLine("\n══ 2. Barrido TopK × tope de fragmentos por documento (híbrido) ══");
        Console.WriteLine("   Sube TopK/tope mientras 'tokens' siga dentro del presupuesto (ver 'eval retrieval' > presupuesto en `retrieve`).");
        PrintHeader();
        var grid = new[] { (4, 3), (6, 3), (6, 5), (8, 3), (8, 5), (8, 8), (10, 5) };
        foreach (var (topK, cap) in grid)
        {
            var variant = Make(("TopK", topK.ToString(CultureInfo.InvariantCulture)), ("MaxChunksPerDocument", cap.ToString(CultureInfo.InvariantCulture)));
            PrintRow(RetrievalMetrics.Summarize($"TopK {topK,2} tope {cap} · original", await RunSet(variant, answerable, Raw, RetrievalMode.Hybrid)));
            PrintRow(RetrievalMetrics.Summarize($"TopK {topK,2} tope {cap} · reescrita", await RunSet(variant, answerable, Rewritten, RetrievalMode.Hybrid)));
        }

        // ── 3. Per case ─────────────────────────────────────────────────────────────────
        Console.WriteLine("\n══ 3. Por caso (configuración de la app) ══");
        Console.WriteLine($"{"caso",-22} {"tipo",-11} {"original",-17} {"reescrita",-17}");
        Console.WriteLine($"{"",-22} {"",-11} {"dist  evid(rango)",-17} {"dist  evid(rango)",-17}");
        for (var i = 0; i < answerable.Count; i++)
            Console.WriteLine($"{answerable[i].Id,-22} {answerable[i].Kind,-11} {Cell(hybridRaw![i]),-17} {Cell(hybridRewrite![i]),-17}");

        var offTopic = await RunSet(baseline, unanswerable, Raw, RetrievalMode.Hybrid);
        foreach (var r in offTopic)
            Console.WriteLine($"{r.Case.Id,-22} {r.Case.Kind,-11} {Dist(r),-17} {"(sin reescritura)",-17}");

        // ── 4. Threshold for the guard ───────────────────────────────────────────────────
        Console.WriteLine("\n══ 4. Umbral de distancia para el guard (Guard:MaxBestDistance) ══");
        Console.WriteLine("   Se rechaza cuando la mejor distancia coseno > umbral. 'conserva' = preguntas respondibles que NO se rechazan;");
        Console.WriteLine("   'rechaza' = preguntas sin respuesta que SÍ se rechazan. Se busca el mejor equilibrio (empate: umbral más bajo).");
        var unanswerableDistances = offTopic.Select(r => r.Result.Diagnostics.BestVectorDistance).ToList();
        double? chosen = null;
        foreach (var (label, runs) in new[] { ("Estrategia ContextProvider (consulta = pregunta original)", hybridRaw!), ("Estrategia Tool (consulta reescrita por el modelo)", hybridRewrite!) })
        {
            var sweep = ThresholdCalibrator.Sweep(runs.Select(r => r.Result.Diagnostics.BestVectorDistance).ToList(), unanswerableDistances, 0.30, 0.70, 0.02);
            var best = ThresholdCalibrator.Best(sweep);
            Console.WriteLine($"\n   {label}");
            Console.WriteLine($"   {"umbral",7} {"conserva",9} {"rechaza",8} {"equilibrio",11}");
            foreach (var p in sweep.Where(p => (int)Math.Round(p.Threshold * 100) % 4 == 2 || Math.Abs(p.Threshold - best.Threshold) < 1e-9))
                Console.WriteLine($"   {p.Threshold,7:0.00} {p.AnswerableKept,9:P0} {p.UnanswerableRefused,8:P0} {p.BalancedAccuracy,11:P0}{(Math.Abs(p.Threshold - best.Threshold) < 1e-9 ? "   <== mejor" : "")}");
            Console.WriteLine($"   Mejor umbral: {best.Threshold:0.00} (conserva {best.AnswerableKept:P0}, rechaza {best.UnanswerableRefused:P0}).");
            chosen ??= best.Threshold;
        }

        Console.WriteLine($"\nSugerencia: Guard:MaxBestDistance = {chosen:0.00} si el guard actúa sobre la consulta del modelo (Tool) o la del usuario (ContextProvider): usa el de tu estrategia.");
        Console.WriteLine($"Embeddings de consulta: {embedder.Misses} calculados, {embedder.Hits} reutilizados de la caché.");
        return 0;


    }

    private static string Cell(CaseRetrieval r) =>
        $"{r.Result.Diagnostics.BestVectorDistance?.ToString("0.000", CultureInfo.InvariantCulture) ?? "  -  "}  {(r.EvidenceHit ? $"sí(#{r.FirstEvidenceRank})" : r.DocumentHit ? "doc" : "NO")}";

    private static string Dist(CaseRetrieval r) =>
        r.Result.Diagnostics.BestVectorDistance?.ToString("0.000", CultureInfo.InvariantCulture) ?? "-";

    private static void PrintHeader() =>
        Console.WriteLine($"   {"configuración",-36} {"evidencia",9} {"documento",9} {"MRR",5} {"tokens",7} {"ms*",6}");

    private static void PrintRow(RetrievalSummary s) =>
        Console.WriteLine($"   {s.Label,-36} {s.EvidenceHit,9:P0} {s.DocHit,9:P0} {s.Mrr,5:0.00} {s.MeanContextTokens,7:0} {s.MeanRetrievalTime.TotalMilliseconds,6:0}");

    // ───────────────────────────────────────────────────────────────────────────────────────
    // eval agent
    // ───────────────────────────────────────────────────────────────────────────────────────

    private static async Task<int> AgentAsync(IServiceProvider sp, string[] args, CancellationToken ct)
    {
        var opts = ParseOptions(args);
        if (opts.Error is not null) return Fail(opts.Error);

        var cases = GoldenSet.Load(opts.GoldenPath).ToList();
        if (opts.Only.Count > 0) cases = cases.Where(c => opts.Only.Contains(c.Id)).ToList();
        if (opts.Limit is { } limit) cases = cases.Take(limit).ToList();
        if (cases.Count == 0) return Fail("Ningún caso coincide con el filtro.");

        var agent = sp.GetRequiredService<IOptions<AgentOptions>>().Value;
        var guard = sp.GetRequiredService<IOptions<GuardOptions>>().Value;
        var retrieval = sp.GetRequiredService<IOptions<RetrievalOptions>>().Value;
        Console.WriteLine($"Evaluando {cases.Count} casos | estrategia {agent.Retrieval}, razonamiento {agent.Reasoning}, TopK {retrieval.TopK}, tope {retrieval.MaxChunksPerDocument}, " +
                          $"guard {(guard.Enabled ? $"≤{guard.MaxBestDistance:0.00}" : "apagado")}{(opts.Judge ? ", juez LLM" : "")}");
        Console.WriteLine("Cada pregunta es una conversación nueva. En este hardware, ~1 min por pregunta con respuesta.\n");

        Directory.CreateDirectory(opts.OutDirectory);
        var outPath = Path.Combine(opts.OutDirectory, $"agent-{DateTime.Now:yyyyMMdd-HHmmss}.jsonl");
        await using var output = new StreamWriter(outPath, false, new UTF8Encoding(false));

        using var tracer = new LatencyTracer(AiServiceCollectionExtensions.TelemetrySourceName, SunatTelemetry.RetrievalSourceName);
        var judge = opts.Judge ? new LlmGroundingJudge(sp.GetRequiredService<IChatClient>()) : null;
        var rows = new List<Row>();

        foreach (var golden in cases)
        {
            ct.ThrowIfCancellationRequested();
            using var scope = sp.CreateScope();
            var chat = scope.ServiceProvider.GetRequiredService<SunatChatSession>();

            var answer = new StringBuilder();
            var tools = new List<string>();
            AnswerAudit? audit = null;
            string? error = null;

            tracer.Begin();
            var sw = Stopwatch.StartNew();
            try
            {
                await foreach (var ev in chat.AskAsync(golden.Question, ct))
                {
                    switch (ev)
                    {
                        case TextDelta d: answer.Append(d.Text); break;
                        case ToolCalled t: tools.Add(t.Name); break;
                        // Judge what the user is finally shown: if the output guard replaced the text, the replacement is the answer.
                        case AnswerReplaced r: answer.Clear().Append(r.Text); break;
                        case AuditReported a: audit = a.Audit; break;
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                error = ex.Message;
            }

            sw.Stop();
            var latency = tracer.Collect(sw.Elapsed);
            var sources = chat.Citations.Snapshot();

            CaseVerdict verdict;
            if (audit is null)
            {
                verdict = new CaseVerdict(false, [$"error: {error ?? "la conversación no terminó"}"]);
            }
            else
            {
                verdict = AnswerChecks.Evaluate(
                    golden, answer.ToString(),
                    audit.Citations.Valid.Select(c => c.Number).ToList(), audit.Citations.Invalid, sources, audit.Grounding);
            }

            JudgeVerdict? judged = null;
            if (judge is not null && audit is not null && golden.ExpectsAnswer && answer.Length > 0)
            {
                var cited = sources.Where(s => audit.Citations.Valid.Any(c => c.Number == s.Number)).ToList();
                judged = await judge.JudgeAsync(golden.Question, answer.ToString(), cited, ct);
            }

            var row = new Row(golden, answer.ToString(), tools, audit, verdict, latency, judged);
            rows.Add(row);
            PrintRow(row);
            await output.WriteLineAsync(JsonSerializer.Serialize(ToJson(row), JsonLines));
            await output.FlushAsync(ct);
        }

        PrintSummary(rows, judge is not null);
        Console.WriteLine($"\nResultados por caso: {outPath}");
        return rows.All(r => r.Verdict.Pass) ? 0 : 3;
    }

    private sealed record Row(
        GoldenCase Case, string Answer, IReadOnlyList<string> Tools, AnswerAudit? Audit, CaseVerdict Verdict, LatencyBreakdown Latency, JudgeVerdict? Judge);

    private static void PrintRow(Row r)
    {
        var l = r.Latency;
        Console.WriteLine(
            $"[{(r.Verdict.Pass ? "OK " : "FALLA")}] {r.Case.Id,-22} {l.Wall.TotalSeconds,6:0.0}s  modelo {l.ModelTime.TotalSeconds,5:0.0}s ({l.ModelCalls} llam.)  " +
            $"búsqueda {l.RetrievalTime.TotalMilliseconds,5:0}ms  herramientas: {(r.Tools.Count == 0 ? "ninguna" : string.Join(",", r.Tools))}");
        foreach (var f in r.Verdict.Failures) Console.WriteLine($"        - {f}");
        if (r.Judge is { } j)
            Console.WriteLine($"        juez: {(j.Supported is { } s ? (s ? "respaldada" : "NO respaldada") : "sin veredicto")} ({j.Elapsed.TotalSeconds:0.0}s)");
    }

    private static void PrintSummary(IReadOnlyList<Row> rows, bool judged)
    {
        Console.WriteLine("\n══ Resumen ══");
        Console.WriteLine($"{"tipo",-12} {"casos",6} {"ok",4} {"%",5}   {"latencia p50",13} {"p95",7}  {"llamadas",8}");
        foreach (var group in rows.GroupBy(r => r.Case.Kind).OrderBy(g => g.Key))
        {
            var walls = group.Select(r => r.Latency.Wall.TotalSeconds).Order().ToList();
            var pass = group.Count(r => r.Verdict.Pass);
            Console.WriteLine(
                $"{group.Key,-12} {group.Count(),6} {pass,4} {pass / (double)group.Count(),5:P0}   {Percentile(walls, 0.5),12:0.0}s {Percentile(walls, 0.95),6:0.0}s  {group.Average(r => r.Latency.ModelCalls),8:0.0}");
        }

        var all = rows.Select(r => r.Latency.Wall.TotalSeconds).Order().ToList();
        var total = rows.Count(r => r.Verdict.Pass);
        Console.WriteLine($"{"TOTAL",-12} {rows.Count,6} {total,4} {total / (double)rows.Count,5:P0}   {Percentile(all, 0.5),12:0.0}s {Percentile(all, 0.95),6:0.0}s");

        var audited = rows.Where(r => r.Audit is not null).ToList();
        Console.WriteLine("\nGroundedness determinista (todas las respuestas):");
        Console.WriteLine($"  con cifras sin respaldo: {audited.Count(r => r.Audit!.Grounding.UngroundedNumbers.Count > 0)}/{audited.Count}");
        Console.WriteLine($"  con citas inexistentes : {audited.Count(r => r.Audit!.Citations.Invalid.Count > 0)}/{audited.Count}");
        Console.WriteLine($"  con afirmaciones sin cita: {audited.Count(r => r.Audit!.Grounding.UncitedClaims.Count > 0)}/{audited.Count}");
        Console.WriteLine($"  con soporte léxico débil : {audited.Count(r => r.Audit!.Grounding.WeakSupport.Count > 0)}/{audited.Count}");
        Console.WriteLine($"  sin aviso educativo      : {audited.Count(r => !r.Audit!.HasDisclaimer && !r.Audit.LooksLikeRefusal)}/{audited.Count}");

        var withCalls = rows.Where(r => r.Latency.PromptTokensPerCall.Count > 0).ToList();
        if (withCalls.Count > 0)
            Console.WriteLine($"\nTokens de prompt de la última llamada: media {withCalls.Average(r => r.Latency.PromptTokensPerCall[^1]):0}, máx {withCalls.Max(r => r.Latency.PromptTokensPerCall[^1])}");

        if (!judged) return;
        var jr = rows.Where(r => r.Judge?.Supported is not null).ToList();
        if (jr.Count == 0) return;
        Console.WriteLine("\nJuez LLM vs. veredicto determinista (casos con respuesta):");
        Console.WriteLine($"  coinciden {jr.Count(r => r.Judge!.Supported == r.Verdict.Pass)}/{jr.Count}; latencia media del juez {jr.Average(r => r.Judge!.Elapsed.TotalSeconds):0.0}s");
        foreach (var r in jr.Where(r => r.Judge!.Supported != r.Verdict.Pass))
            Console.WriteLine($"  discrepa: {r.Case.Id} (juez {(r.Judge!.Supported!.Value ? "respaldada" : "no respaldada")}, reglas {(r.Verdict.Pass ? "OK" : "falla")})");
    }

    private static object ToJson(Row r) => new
    {
        id = r.Case.Id,
        kind = r.Case.Kind.ToString(),
        question = r.Case.Question,
        answer = r.Answer,
        pass = r.Verdict.Pass,
        failures = r.Verdict.Failures,
        tools = r.Tools,
        wallSeconds = Math.Round(r.Latency.Wall.TotalSeconds, 2),
        modelSeconds = Math.Round(r.Latency.ModelTime.TotalSeconds, 2),
        toolSeconds = Math.Round(r.Latency.ToolTime.TotalSeconds, 2),
        retrievalMs = Math.Round(r.Latency.RetrievalTime.TotalMilliseconds),
        modelCalls = r.Latency.ModelCalls,
        promptTokens = r.Latency.PromptTokensPerCall,
        validCitations = r.Audit?.Citations.Valid.Select(c => c.Number),
        invalidCitations = r.Audit?.Citations.Invalid,
        ungroundedNumbers = r.Audit?.Grounding.UngroundedNumbers,
        uncitedClaims = r.Audit?.Grounding.UncitedClaims.Count,
        weakSupport = r.Audit?.Grounding.WeakSupport.Select(w => new { w.Sentence, w.Cited, overlap = Math.Round(w.Overlap, 2) }),
        judge = r.Judge is null ? null : new { supported = r.Judge.Supported, reason = r.Judge.Reason, seconds = Math.Round(r.Judge.Elapsed.TotalSeconds, 1) }
    };

    private static double Percentile(IReadOnlyList<double> sorted, double p) =>
        sorted.Count == 0 ? double.NaN : sorted[Math.Clamp((int)Math.Ceiling(p * sorted.Count) - 1, 0, sorted.Count - 1)];

    // ───────────────────────────────────────────────────────────────────────────────────────

    private sealed record EvalOptions(string GoldenPath, HashSet<string> Only, int? Limit, bool Judge, string OutDirectory, string? Error);

    private static EvalOptions ParseOptions(string[] args)
    {
        var golden = Path.Combine("eval", "golden.jsonl");
        var only = new HashSet<string>(StringComparer.Ordinal);
        int? limit = null;
        var judge = false;
        var outDir = Path.Combine("data", "eval");

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--golden" when i + 1 < args.Length: golden = args[++i]; break;
                case "--only" when i + 1 < args.Length:
                    foreach (var id in args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) only.Add(id);
                    break;
                case "--limit" when i + 1 < args.Length && int.TryParse(args[i + 1], out var n): limit = n; i++; break;
                case "--judge": judge = true; break;
                case "--out" when i + 1 < args.Length: outDir = args[++i]; break;
                default: return new EvalOptions(golden, only, limit, judge, outDir, $"Argumento no válido: {args[i]}");
            }
        }

        return File.Exists(golden)
            ? new EvalOptions(golden, only, limit, judge, outDir, null)
            : new EvalOptions(golden, only, limit, judge, outDir, $"No existe {golden}. Ejecuta desde la raíz del repo o usa --golden.");
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        Console.Error.WriteLine(Usage);
        return 1;
    }
}
