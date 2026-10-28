using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SunatRAG.Core.Documents;

namespace SunatRAG.Ingestion.Scraping;

public sealed record ScrapeRequest(int FromYear, int ToYear, int? Limit, bool IncludeAnexos, bool RefreshIndex);

public sealed class ScrapeSummary
{
    public int IndexPages { get; set; }
    public int Listed { get; set; }
    public int Downloaded { get; set; }
    public int AlreadyDownloaded { get; set; }
    public int DuplicateContent { get; set; }
    public int SkippedNonPdf { get; set; }
    public bool LimitReached { get; set; }
    public RateLimitedException? RateLimit { get; set; }
    public List<(Uri Url, string Error)> Failures { get; } = [];
}

/// <summary>
/// Orchestrates: seed pages -> per-year indexes -> parse rows -> download PDFs -> manifest.
/// Resumable by construction: anything already in the manifest (and still on disk) is skipped.
/// </summary>
public sealed class ScrapeRunner(
    IndexPageCache indexCache,
    PdfDownloader downloader,
    ManifestStore manifest,
    DataPaths paths,
    IOptions<ScrapingOptions> options,
    TimeProvider time,
    ILogger<ScrapeRunner> logger)
{
    private sealed record Target(
        string Id, Uri Url, DocumentRole Role, string? ParentId, string? AnexoLabel, NormaListing Norma);

    public async Task<ScrapeSummary> RunAsync(ScrapeRequest request, CancellationToken ct)
    {
        var summary = new ScrapeSummary();
        var baseUri = new Uri(options.Value.BaseUrl);

        var existing = await manifest.LoadAsync(ct);
        var onDisk = existing.Values
            .Where(d => File.Exists(Path.Combine(paths.Root, d.RelativePath)))
            .Select(d => d.Id)
            .ToHashSet(StringComparer.Ordinal);
        var idByHash = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var d in existing.Values) idByHash.TryAdd(d.Sha256, d.Id);

        try
        {
            var pages = await DiscoverAsync(baseUri, request, ct);
            summary.IndexPages = pages.Count;
            logger.LogInformation("{Count} páginas de índice entre {From} y {To}.", pages.Count, request.FromYear, request.ToYear);

            foreach (var page in pages)
            {
                ct.ThrowIfCancellationRequested();

                ParsedIndex parsed;
                try
                {
                    var html = await indexCache.GetAsync(page.Url, request.RefreshIndex, ct);
                    parsed = SunatIndexParser.ParseNormaIndex(html, page.Url, page.Tipo, page.Year);
                }
                catch (HttpRequestException ex)
                {
                    summary.Failures.Add((page.Url, ex.Message));
                    logger.LogWarning("No se pudo leer el índice {Url}: {Error}", page.Url, ex.Message);
                    continue;
                }

                summary.SkippedNonPdf += parsed.SkippedNonPdf;
                logger.LogInformation("{Year} {Tipo}: {Count} normas con PDF ({Skipped} sin PDF omitidas).",
                    page.Year, page.Tipo, parsed.Normas.Count, parsed.SkippedNonPdf);

                foreach (var norma in parsed.Normas)
                {
                    summary.Listed++;
                    foreach (var target in TargetsFor(norma, request.IncludeAnexos))
                    {
                        if (onDisk.Contains(target.Id)) { summary.AlreadyDownloaded++; continue; }

                        if (request.Limit is { } limit && summary.Downloaded >= limit)
                        {
                            summary.LimitReached = true;
                            return summary;
                        }

                        await TryDownloadAsync(target, summary, idByHash, ct);
                    }
                }
            }
        }
        catch (RateLimitedException ex)
        {
            summary.RateLimit = ex;
            logger.LogError("HTTP 429 en {Url}. Ejecución detenida; reintenta más tarde (Retry-After: {RetryAfter}).",
                ex.Url, ex.RetryAfter);
        }

        return summary;
    }

    private async Task<IReadOnlyList<IndexPage>> DiscoverAsync(Uri baseUri, ScrapeRequest request, CancellationToken ct)
    {
        var found = new Dictionary<Uri, IndexPage>();
        foreach (var seed in SunatSources.SeedPages)
        {
            var seedUrl = new Uri(baseUri, seed);
            var html = await indexCache.GetAsync(seedUrl, request.RefreshIndex, ct);
            foreach (var page in SunatIndexParser.DiscoverIndexPages(html, seedUrl)) found[page.Url] = page;
        }

        return [.. found.Values
            .Where(p => p.Year >= request.FromYear && p.Year <= request.ToYear)
            .OrderByDescending(p => p.Year)
            .ThenBy(p => p.Tipo)];
    }

    private static IEnumerable<Target> TargetsFor(NormaListing norma, bool includeAnexos)
    {
        yield return new Target(norma.Id, norma.Url, DocumentRole.Principal, null, null, norma);
        if (!includeAnexos) yield break;

        foreach (var anexo in norma.Anexos)
            yield return new Target(NormaIds.FromUrl(anexo.Url), anexo.Url, DocumentRole.Anexo, norma.Id, anexo.Label, norma);
    }

    private async Task TryDownloadAsync(
        Target target, ScrapeSummary summary, Dictionary<string, string> idByHash, CancellationToken ct)
    {
        try
        {
            var result = await downloader.DownloadAsync(target.Url, ct);
            var stored = result.Stored;

            var doc = new NormaDocument(
                target.Id, target.Role, target.ParentId, target.AnexoLabel, target.Norma, target.Url,
                stored.Sha256, stored.RelativePath, stored.SizeBytes, time.GetUtcNow(),
                result.ETag, result.LastModified);
            await manifest.AppendAsync(doc, ct);

            summary.Downloaded++;
            if (stored.IsDuplicate)
            {
                summary.DuplicateContent++;
                idByHash.TryGetValue(stored.Sha256, out var other);
                logger.LogInformation("{Id}: contenido idéntico a {Other}; se reutiliza el archivo.", target.Id, other ?? "una descarga anterior");
            }
            else
            {
                idByHash[stored.Sha256] = target.Id;
                logger.LogInformation("{Id}: {Kb} KB", target.Id, stored.SizeBytes / 1024);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or RobotsDisallowedException)
        {
            summary.Failures.Add((target.Url, ex.Message));
            logger.LogWarning("Falló {Url}: {Error}", target.Url, ex.Message);
        }
    }
}
