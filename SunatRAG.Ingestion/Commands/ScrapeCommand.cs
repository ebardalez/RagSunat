using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SunatRAG.Ingestion.Scraping;

namespace SunatRAG.Ingestion.Commands;

/// <summary>Phase 1: dotnet run --project SunatRAG.Ingestion -- scrape [--from 2024] [--to 2026] [--limit 20] [--anexos] [--refresh-index]</summary>
public static class ScrapeCommand
{
    public const string Usage =
        "Uso: dotnet run --project SunatRAG.Ingestion -- scrape [--from AAAA] [--to AAAA] [--limit N] [--anexos] [--refresh-index]";

    public static async Task<int> RunAsync(IServiceProvider sp, string[] args, CancellationToken ct)
    {
        var options = sp.GetRequiredService<IOptions<ScrapingOptions>>().Value;
        var fromYear = options.FromYear;
        var toYear = options.ToYear ?? DateTime.UtcNow.Year;
        int? limit = null;
        var anexos = options.IncludeAnexos;
        var refresh = false;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--from" when TryReadInt(args, ref i, out var v): fromYear = v; break;
                case "--to" when TryReadInt(args, ref i, out var v): toYear = v; break;
                case "--limit" when TryReadInt(args, ref i, out var v): limit = v; break;
                case "--anexos": anexos = true; break;
                case "--refresh-index": refresh = true; break;
                default:
                    Console.Error.WriteLine($"Argumento no válido: {args[i]}");
                    Console.Error.WriteLine(Usage);
                    return 1;
            }
        }

        if (fromYear > toYear)
        {
            Console.Error.WriteLine("--from no puede ser mayor que --to.");
            return 1;
        }

        var paths = sp.GetRequiredService<DataPaths>();
        Console.WriteLine($"Rango {fromYear}-{toYear}, límite {(limit?.ToString() ?? "sin límite")}, anexos: {(anexos ? "sí" : "no")}.");
        Console.WriteLine($"Intervalo mínimo {options.MinRequestInterval.TotalSeconds:0.#} s entre peticiones. Datos en {paths.Root}");

        var runner = sp.GetRequiredService<ScrapeRunner>();
        var s = await runner.RunAsync(new ScrapeRequest(fromYear, toYear, limit, anexos, refresh), ct);

        Console.WriteLine();
        Console.WriteLine("── Resumen ──────────────────────────────");
        Console.WriteLine($"Índices leídos:        {s.IndexPages}");
        Console.WriteLine($"Normas listadas:       {s.Listed}");
        Console.WriteLine($"PDF descargados:       {s.Downloaded} (contenido duplicado: {s.DuplicateContent})");
        Console.WriteLine($"Ya existentes (skip):  {s.AlreadyDownloaded}");
        Console.WriteLine($"Sin PDF (omitidas):    {s.SkippedNonPdf}");
        Console.WriteLine($"Fallos:                {s.Failures.Count}");
        foreach (var (url, error) in s.Failures.Take(10)) Console.WriteLine($"  - {url}: {error}");
        if (s.LimitReached) Console.WriteLine("Se alcanzó --limit; vuelve a ejecutar para continuar (es reanudable).");
        if (s.RateLimit is not null) Console.WriteLine("Detenido por HTTP 429. Espera antes de reintentar.");

        if (s.RateLimit is not null) return 2;
        return s.Failures.Count > 0 ? 3 : 0;
    }

    private static bool TryReadInt(string[] args, ref int i, out int value)
    {
        value = 0;
        if (i + 1 >= args.Length) return false;
        if (!int.TryParse(args[i + 1], out value)) return false;
        i++;
        return true;
    }
}
