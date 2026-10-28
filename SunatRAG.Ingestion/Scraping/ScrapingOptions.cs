namespace SunatRAG.Ingestion.Scraping;

public sealed class ScrapingOptions
{
    public const string SectionName = "Scraping";

    /// <summary>Politeness floor. Validation refuses anything below this (project rule: 1 req/sec).</summary>
    public static readonly TimeSpan MinimumInterval = TimeSpan.FromSeconds(1);

    public string BaseUrl { get; init; } = "https://www.sunat.gob.pe/";

    /// <summary>Identifies the project to the site operator. Add <see cref="Contact"/> so they can reach you.</summary>
    public string UserAgent { get; init; } = "SunatRAG/0.1 (educational RAG learning project; non-commercial)";

    /// <summary>Optional e-mail or URL appended to the User-Agent as "contact: ...".</summary>
    public string? Contact { get; init; }

    public TimeSpan MinRequestInterval { get; init; } = MinimumInterval;
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(60);
    public int MaxRetries { get; init; } = 3;
    public TimeSpan RetryBaseDelay { get; init; } = TimeSpan.FromSeconds(2);

    public int FromYear { get; init; } = 2024;

    /// <summary>Null = current year.</summary>
    public int? ToYear { get; init; }

    /// <summary>Also download PDFs linked from the sumilla cell (anexos). Off by default: they can be large forms.</summary>
    public bool IncludeAnexos { get; init; }

    /// <summary>Relative paths resolve against the current directory (run from the repo root).</summary>
    public string DataDirectory { get; init; } = "data";

    public TimeSpan IndexCacheTtl { get; init; } = TimeSpan.FromHours(24);

    public string BuildUserAgent() =>
        string.IsNullOrWhiteSpace(Contact) ? UserAgent : $"{UserAgent}; contact: {Contact}";

    /// <summary>The token robots.txt groups are matched against ("SunatRAG").</summary>
    public string ProductToken => UserAgent.Split(['/', ' '], StringSplitOptions.RemoveEmptyEntries)[0];
}

/// <summary>Where everything lives on disk. One place, so tests can point it at a temp folder.</summary>
public sealed record DataPaths(string Root)
{
    public string Manifest => Path.Combine(Root, "manifest.jsonl");
    public string Pdfs => Path.Combine(Root, "pdfs");
    public string IndexCache => Path.Combine(Root, "cache", "index");
    public string Temp => Path.Combine(Root, "tmp");
}

/// <summary>Where the site's legislación indexes start. These are site structure, not configuration.</summary>
public static class SunatSources
{
    /// <summary>
    /// legislacion/general lists the resoluciones de superintendencia by year;
    /// legislacion/tributaria lists informes y oficios by year.
    /// (legislacion/superin/index.html is only a JavaScript redirect to legislacion/general.)
    /// </summary>
    public static readonly string[] SeedPages =
    [
        "legislacion/general/index.html",
        "legislacion/tributaria/index.html"
    ];
}

public sealed class RateLimitedException(Uri url, TimeSpan? retryAfter)
    : Exception($"HTTP 429 de {url}. Se detiene la ejecución (regla del proyecto: parar ante 429).")
{
    public Uri Url { get; } = url;
    public TimeSpan? RetryAfter { get; } = retryAfter;
}

public sealed class RobotsDisallowedException(Uri url)
    : Exception($"robots.txt no permite acceder a {url}.")
{
    public Uri Url { get; } = url;
}
