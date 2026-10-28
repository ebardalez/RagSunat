using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SunatRAG.Ingestion.Extraction;
using SunatRAG.Ingestion.Scraping;

namespace SunatRAG.Ingestion.Commands;

/// <summary>Phase 2: manifest.jsonl -> chunks.jsonl (+ extraction-report.jsonl).</summary>
public static class ExtractCommand
{
    public const string Usage =
        "Uso: dotnet run --project SunatRAG.Ingestion -- extract [--include-admin] [--approx-tokens] [--only <id>] [--limit N] [--target N] [--overlap N]";

    public static async Task<int> RunAsync(IServiceProvider sp, string[] args, CancellationToken ct)
    {
        var options = sp.GetRequiredService<IOptions<ChunkingOptions>>().Value;
        var includeAdmin = false;
        var approx = false;
        string? only = null;
        int? limit = null;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--include-admin": includeAdmin = true; break;
                case "--approx-tokens": approx = true; break;
                case "--only" when i + 1 < args.Length: only = args[++i]; break;
                case "--limit" when i + 1 < args.Length && int.TryParse(args[i + 1], out var l): limit = l; i++; break;
                case "--target" when i + 1 < args.Length && int.TryParse(args[i + 1], out var t): options = options with { TargetTokens = t }; i++; break;
                case "--overlap" when i + 1 < args.Length && int.TryParse(args[i + 1], out var o): options = options with { OverlapTokens = o }; i++; break;
                default:
                    Console.Error.WriteLine($"Argumento no válido: {args[i]}");
                    Console.Error.WriteLine(Usage);
                    return 1;
            }
        }

        ITokenCounter counter;
        try
        {
            counter = approx ? new ApproximateTokenCounter() : sp.GetRequiredService<ITokenCounter>();
        }
        catch (FileNotFoundException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }

        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.WriteLine($"Tokenizador: {counter.GetType().Name} | objetivo {options.TargetTokens} tokens, solape {options.OverlapTokens}.");
        Console.WriteLine($"Administrativas: {(includeAdmin ? "incluidas" : "excluidas")}.");

        var runner = sp.GetRequiredService<ChunkingRunner>();
        var chunker = new TokenAwareChunker(counter, options);
        var started = DateTime.UtcNow;
        var summary = await runner.RunAsync(chunker, new ChunkingRequest(includeAdmin, limit, only), ct);
        var elapsed = DateTime.UtcNow - started;

        if (only is not null) PrintChunks(summary);
        PrintSummary(summary, elapsed, runner);
        return summary.Reports.Any(r => r.Status == nameof(ExtractionStatus.Failed)) ? 3 : 0;
    }

    private static void PrintChunks(ChunkingSummary summary)
    {
        foreach (var c in summary.Chunks)
        {
            Console.WriteLine();
            Console.WriteLine($"══ {c.ChunkId} | {c.TokenCount} tok | págs {c.PageStart}-{c.PageEnd} | {c.Heading ?? "(preámbulo)"}");
            Console.WriteLine(c.Text.Length <= 600 ? c.Text : c.Text[..600] + " […]");
        }
    }

    private static void PrintSummary(ChunkingSummary s, TimeSpan elapsed, ChunkingRunner runner)
    {
        var byStatus = s.Reports.GroupBy(r => r.Status).OrderBy(g => g.Key);
        Console.WriteLine();
        Console.WriteLine("── Resumen ──────────────────────────────");
        Console.WriteLine($"Documentos en el manifest procesados: {s.Reports.Count} ({elapsed.TotalSeconds:0.0} s)");
        foreach (var g in byStatus) Console.WriteLine($"  {g.Key,-24} {g.Count()}");

        var admin = s.Reports.Where(r => r.Status == "SkippedAdministrativa")
            .GroupBy(r => r.CategoriaRule).OrderByDescending(g => g.Count());
        foreach (var g in admin) Console.WriteLine($"    regla {g.Key,-32} {g.Count()}");

        foreach (var r in s.Reports.Where(r => r.Status is "NoText" or "Failed").Take(10))
            Console.WriteLine($"  ! {r.Id}: {r.Status} {r.Detail}");

        if (s.Chunks.Count == 0) return;

        var tokens = s.Chunks.Select(c => c.TokenCount).Order().ToArray();
        Console.WriteLine();
        Console.WriteLine($"Chunks: {tokens.Length} | tokens totales: {tokens.Sum(t => (long)t):N0}");
        Console.WriteLine($"Tokens por chunk  min {tokens[0]} | mediana {tokens[tokens.Length / 2]} | p95 {tokens[(int)(tokens.Length * 0.95)]} | max {tokens[^1]}");
        Console.WriteLine($"Chunks con encabezado detectado: {s.Chunks.Count(c => c.Heading is not null) * 100 / s.Chunks.Count} %");
        Console.WriteLine($"Chunks por documento: media {(double)s.Chunks.Count / s.Reports.Count(r => r.Status == "Ok"):0.0}");
        Console.WriteLine(s.WroteFiles
            ? $"Escrito: {runner.ChunksFile} y {runner.ReportFile}"
            : "Corrida parcial (--only/--limit): no se escribieron archivos.");
    }
}
