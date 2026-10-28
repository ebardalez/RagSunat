using Microsoft.Extensions.DependencyInjection;
using SunatRAG.Ingestion.Extraction;
using SunatRAG.Ingestion.Scraping;

namespace SunatRAG.Ingestion.Commands;

/// <summary>
/// Debug tool: dumps what PdfPig sees for one document, with line breaks made visible.
/// Look at real text BEFORE writing splitting rules; that is how the heading regexes in Phase 2 were calibrated.
/// </summary>
public static class InspectCommand
{
    public const string Usage =
        "Uso: dotnet run --project SunatRAG.Ingestion -- inspect <id-documento> [--pages N]";

    public static async Task<int> RunAsync(IServiceProvider sp, string[] args, CancellationToken ct)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine(Usage);
            return 1;
        }

        var id = args[0];
        var maxPages = 2;
        var contentOrder = true;
        for (var i = 1; i < args.Length; i++)
        {
            if (args[i] == "--stream") contentOrder = false;
            else if (args[i] == "--pages" && i + 1 < args.Length && int.TryParse(args[++i], out var n)) maxPages = n;
            else
            {
                Console.Error.WriteLine(Usage);
                return 1;
            }
        }

        var paths = sp.GetRequiredService<DataPaths>();
        var manifest = await sp.GetRequiredService<ManifestStore>().LoadAsync(ct);
        if (!manifest.TryGetValue(id, out var doc))
        {
            Console.Error.WriteLine($"No existe '{id}' en el manifest.");
            return 1;
        }

        var result = new PdfTextExtractor(new PdfExtractionOptions { UseContentOrder = contentOrder }).Extract(Path.Combine(paths.Root, doc.RelativePath), ct);
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.WriteLine($"{doc.Norma.Titulo} | estado={result.Status} páginas={result.PageCount} vacías={result.EmptyPages}");
        Console.WriteLine($"Sumilla: {doc.Norma.Sumilla}");

        foreach (var page in result.Pages.Take(maxPages))
        {
            Console.WriteLine($"──────── página {page.Number} ({page.Text.Length} chars) ────────");
            Console.WriteLine(page.Text.Replace("\r", "").Replace("\n", "⏎\n"));
        }

        return 0;
    }
}
