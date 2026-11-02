using System.Globalization;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SunatRAG.Core.Configuration;
using SunatRAG.Core.Documents;
using SunatRAG.Core.Retrieval;

namespace SunatRAG.Ingestion.Commands;

/// <summary>Phase 4: run the retriever from the command line and show WHY each chunk ranked where it did.</summary>
public static class RetrieveCommand
{
    public const string Usage =
        "Uso: dotnet run --project SunatRAG.Ingestion -- retrieve \"pregunta\" [--mode hybrid|vector|keyword] [--top N]\n" +
        "       [--tipo resolucion|informe] [--year N] [--desde yyyy-mm-dd] [--hasta yyyy-mm-dd]\n" +
        "       [--todas (incluye administrativas)] [--proyectos (incluye proyectos de resolución)] [--rerank] [--context]";

    public static async Task<int> RunAsync(IServiceProvider sp, string[] args, CancellationToken ct)
    {
        string? query = null;
        var mode = RetrievalMode.Hybrid;
        int? top = null, year = null;
        TipoNorma? tipo = null;
        DateOnly? desde = null, hasta = null;
        bool todas = false, proyectos = false, rerank = false, showContext = false;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--mode" when i + 1 < args.Length:
                    if (!TryMode(args[++i], out mode)) return Fail($"Modo no válido: {args[i]}");
                    break;
                case "--top" when i + 1 < args.Length && int.TryParse(args[i + 1], out var t): top = t; i++; break;
                case "--year" when i + 1 < args.Length && int.TryParse(args[i + 1], out var y): year = y; i++; break;
                case "--tipo" when i + 1 < args.Length:
                    if (!TryTipo(args[++i], out var parsed)) return Fail($"Tipo no válido: {args[i]}");
                    tipo = parsed;
                    break;
                case "--desde" when i + 1 < args.Length:
                    if (!DateOnly.TryParse(args[++i], CultureInfo.InvariantCulture, out var d1)) return Fail($"Fecha no válida: {args[i]}");
                    desde = d1;
                    break;
                case "--hasta" when i + 1 < args.Length:
                    if (!DateOnly.TryParse(args[++i], CultureInfo.InvariantCulture, out var d2)) return Fail($"Fecha no válida: {args[i]}");
                    hasta = d2;
                    break;
                case "--todas": todas = true; break;
                case "--proyectos": proyectos = true; break;
                case "--rerank": rerank = true; break;
                case "--context": showContext = true; break;
                case var q when !q.StartsWith("--", StringComparison.Ordinal) && query is null: query = q; break;
                default: return Fail($"Argumento no válido: {args[i]}");
            }
        }

        if (string.IsNullOrWhiteSpace(query)) return Fail("Falta la pregunta.");

        Console.OutputEncoding = Encoding.UTF8;
        var retriever = sp.GetRequiredService<HybridRetriever>();
        var filters = new SearchFilters
        {
            Tipo = tipo,
            Categoria = todas ? null : Categoria.Normativa,
            Year = year,
            PublicadoDesde = desde,
            PublicadoHasta = hasta,
            IncluirProyectos = proyectos
        };

        var result = await retriever.RetrieveAsync(
            new RetrievalRequest(query, filters, top, mode, rerank ? RerankMode.Llm : RerankMode.None), ct);

        Console.WriteLine($"Consulta: \"{result.Query}\" | modo {mode}{(rerank ? " + rerank LLM" : "")}");
        Console.WriteLine();
        var n = 0;
        foreach (var c in result.Chunks)
        {
            var provenance = $"v#{c.VectorRank?.ToString() ?? "-"} k#{c.KeywordRank?.ToString() ?? "-"}";
            var dist = c.VectorDistance is { } dd ? $"dist {dd:0.000}" : "dist  -  ";
            var bm25 = c.Bm25Score is { } bs ? $"bm25 {bs:0.0}" : "bm25 -";
            Console.WriteLine($" {++n}. rrf {c.Score:0.0000} | {provenance,-9} | {dist} | {bm25} | ~{c.ContextTokens} tok");
            Console.WriteLine($"    {c.Cita}");
            var text = c.Record.Text.ReplaceLineEndings(" ");
            Console.WriteLine($"    {(text.Length <= 200 ? text : text[..200] + "…")}");
        }

        if (result.Chunks.Count == 0) Console.WriteLine(" (sin resultados con esos filtros)");
        PrintDiagnostics(result.Diagnostics, sp.GetRequiredService<IOptions<RetrievalOptions>>().Value, result.Query, result.Chunks.Count);

        if (showContext)
        {
            Console.WriteLine();
            Console.WriteLine("── Bloque de contexto tal como lo vería el LLM ──");
            Console.WriteLine(RetrievalContextFormatter.Format(result.Chunks));
        }

        return 0;
    }

    private static void PrintDiagnostics(RetrievalDiagnostics d, RetrievalOptions o, string query, int selected)
    {
        Console.WriteLine();
        Console.WriteLine("── Diagnóstico ──────────────────────────");
        Console.WriteLine($"Tiempos: embedding de la consulta {d.Embed.TotalMilliseconds:0} ms | búsqueda vectorial {d.Vector.TotalMilliseconds:0} ms | " +
                          $"BM25 {d.Keyword.TotalMilliseconds:0} ms | rerank {(d.RerankApplied || d.RerankFellBack ? $"{d.Rerank.TotalSeconds:0.0} s" : "-")}" +
                          (d.RerankFellBack ? " (respuesta inutilizable: orden original)" : ""));
        Console.WriteLine($"Candidatos: vectorial {d.VectorCandidates} | BM25 {d.KeywordCandidates} | fusionados {d.FusedCandidates} | en contexto {selected}");
        if (d.BestVectorDistance is { } best)
            Console.WriteLine($"Mejor distancia coseno: {best:0.000} (referencia para el filtro de 'fuera de alcance' de la Fase 6)");

        var q = ContextBudget.EstimateQuestionTokens(query);
        Console.WriteLine($"Presupuesto de contexto: {d.ContextLength} (ventana) − {o.ReservedSystemTokens} (sistema) − {o.ReservedHistoryTokens} (historial) " +
                          $"− {o.ReservedAnswerTokens} (razonamiento + respuesta) − {q} (pregunta) = {d.BudgetTokens} tokens para fragmentos");
        Console.WriteLine($"Usados: {d.UsedTokens} de {d.BudgetTokens} (cada fragmento = tokens bge-m3 × {o.ChatTokenSafetyFactor:0.00} + {o.PerChunkOverheadTokens}) | " +
                          $"omitidos por tope por documento: {d.SkippedForDocumentCap} | por presupuesto: {d.SkippedForBudget}");
    }

    private static bool TryMode(string s, out RetrievalMode mode)
    {
        (var ok, mode) = s.ToLowerInvariant() switch
        {
            "hybrid" => (true, RetrievalMode.Hybrid),
            "vector" => (true, RetrievalMode.VectorOnly),
            "keyword" => (true, RetrievalMode.KeywordOnly),
            _ => (false, RetrievalMode.Hybrid)
        };
        return ok;
    }

    private static bool TryTipo(string s, out TipoNorma tipo)
    {
        if (s.Equals("resolucion", StringComparison.OrdinalIgnoreCase)) { tipo = TipoNorma.ResolucionSuperintendencia; return true; }
        return Enum.TryParse(s, ignoreCase: true, out tipo);
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        Console.Error.WriteLine(Usage);
        return 1;
    }
}
