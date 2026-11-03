using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SunatRAG.Ingestion.Scraping;

namespace SunatRAG.Tests.Scraping;

/// <summary>Conditional GET (ETag / Last-Modified -> 304) for the index cache. Scripted site, temp folder, no network.</summary>
public class IndexPageCacheRevalidationTests : IDisposable
{
    private static readonly Uri PageUrl = new("https://sunat.test/legislacion/superin/2025/indices/indcor.htm");
    private static readonly DateTimeOffset Modified = new(2026, 1, 15, 10, 0, 0, TimeSpan.Zero);

    private readonly TempDir _dir = new();
    private readonly DataPaths _paths;
    private readonly List<HttpRequestMessage> _seen = [];
    private Func<HttpRequestMessage, HttpResponseMessage> _route = _ => Responses.Status(HttpStatusCode.NotFound);
    private readonly IndexPageCache _cache;

    public IndexPageCacheRevalidationTests()
    {
        _paths = new DataPaths(_dir.Path);
        var options = Options.Create(new ScrapingOptions { BaseUrl = "https://sunat.test/", RetryBaseDelay = TimeSpan.FromMilliseconds(1) });
        var site = new StubHttpHandler(request =>
        {
            _seen.Add(request);
            return _route(request);
        });
        var http = new SunatHttpClient(new HttpClient(site), options, NullLogger<SunatHttpClient>.Instance);
        _cache = new IndexPageCache(_paths, http, options, TimeProvider.System, NullLogger<IndexPageCache>.Instance);
    }

    private static HttpResponseMessage Page(string html, string? etag, DateTimeOffset? modified)
    {
        var response = Responses.Html(html);
        if (etag is not null) response.Headers.ETag = new EntityTagHeaderValue(etag);
        if (modified is not null) response.Content.Headers.LastModified = modified;
        return response;
    }

    private string CachedFile => Directory.GetFiles(_paths.IndexCache, "*.html").Single();

    [Fact]
    public async Task A_304_keeps_the_cached_file_sends_the_saved_etag_and_restarts_the_ttl()
    {
        _route = r => r.Headers.IfNoneMatch.Any(t => t.Tag == "\"v1\"")
            ? Responses.Status(HttpStatusCode.NotModified)
            : Page("<p>índice v1</p>", "\"v1\"", Modified);

        await _cache.GetAsync(PageUrl, refresh: false, CancellationToken.None);
        File.SetLastWriteTimeUtc(CachedFile, DateTime.UtcNow.AddDays(-2)); // older than the 24 h TTL

        var html = await _cache.GetAsync(PageUrl, refresh: false, CancellationToken.None);

        Assert.Equal("<p>índice v1</p>", html);
        Assert.Equal(2, _seen.Count);
        Assert.Contains(_seen[1].Headers.IfNoneMatch, t => t.Tag == "\"v1\""); // the validator saved by the first response
        Assert.True(DateTime.UtcNow - File.GetLastWriteTimeUtc(CachedFile) < TimeSpan.FromMinutes(1)); // timestamp renewed by the 304

        await _cache.GetAsync(PageUrl, refresh: false, CancellationToken.None); // inside the new TTL window
        Assert.Equal(2, _seen.Count); // no request at all
    }

    [Fact]
    public async Task Refresh_revalidates_instead_of_downloading_again()
    {
        _route = r => r.Headers.IfNoneMatch.Count > 0 ? Responses.Status(HttpStatusCode.NotModified) : Page("<p>x</p>", "\"v1\"", null);

        await _cache.GetAsync(PageUrl, refresh: false, CancellationToken.None);
        var html = await _cache.GetAsync(PageUrl, refresh: true, CancellationToken.None);

        Assert.Equal("<p>x</p>", html);
        Assert.Equal(2, _seen.Count);
        Assert.NotEmpty(_seen[1].Headers.IfNoneMatch);
    }

    [Fact]
    public async Task Without_an_etag_it_falls_back_to_if_modified_since()
    {
        _route = r => r.Headers.IfModifiedSince is not null
            ? Responses.Status(HttpStatusCode.NotModified)
            : Page("<p>y</p>", etag: null, Modified);

        await _cache.GetAsync(PageUrl, refresh: false, CancellationToken.None);
        await _cache.GetAsync(PageUrl, refresh: true, CancellationToken.None);

        Assert.Empty(_seen[1].Headers.IfNoneMatch);
        Assert.Equal(Modified, _seen[1].Headers.IfModifiedSince);
    }

    [Fact]
    public async Task A_changed_page_replaces_the_file_and_the_validators()
    {
        var version = 1;
        _route = r =>
        {
            if (version == 1) return Page("<p>viejo</p>", "\"v1\"", null);
            return r.Headers.IfNoneMatch.Any(t => t.Tag == "\"v2\"")
                ? Responses.Status(HttpStatusCode.NotModified)
                : Page("<p>nuevo</p>", "\"v2\"", null);
        };

        await _cache.GetAsync(PageUrl, refresh: false, CancellationToken.None);
        version = 2;
        var changed = await _cache.GetAsync(PageUrl, refresh: true, CancellationToken.None); // sends v1, server answers 200 + v2
        var unchanged = await _cache.GetAsync(PageUrl, refresh: true, CancellationToken.None); // now sends v2 -> 304

        Assert.Equal("<p>nuevo</p>", changed);
        Assert.Equal("<p>nuevo</p>", unchanged);
        Assert.Contains(_seen[2].Headers.IfNoneMatch, t => t.Tag == "\"v2\"");
    }

    [Fact]
    public async Task A_server_without_validators_just_gets_a_plain_get_every_time()
    {
        _route = _ => Page("<p>z</p>", etag: null, modified: null);

        await _cache.GetAsync(PageUrl, refresh: false, CancellationToken.None);
        var html = await _cache.GetAsync(PageUrl, refresh: true, CancellationToken.None);

        Assert.Equal("<p>z</p>", html);
        Assert.All(_seen, r => Assert.Empty(r.Headers.IfNoneMatch));
        Assert.All(_seen, r => Assert.Null(r.Headers.IfModifiedSince));
    }

    [Fact]
    public async Task An_unreadable_sidecar_means_a_plain_get_not_a_crash()
    {
        _route = _ => Page("<p>w</p>", "\"v1\"", null);
        await _cache.GetAsync(PageUrl, refresh: false, CancellationToken.None);
        await File.WriteAllTextAsync(CachedFile + ".meta.json", "{ esto no es json");

        var html = await _cache.GetAsync(PageUrl, refresh: true, CancellationToken.None);

        Assert.Equal("<p>w</p>", html);
        Assert.Empty(_seen[1].Headers.IfNoneMatch);
    }

    [Fact]
    public async Task A_304_for_a_request_that_was_not_conditional_is_an_error()
    {
        _route = _ => Responses.Status(HttpStatusCode.NotModified);

        await Assert.ThrowsAsync<HttpRequestException>(() => _cache.GetAsync(PageUrl, refresh: false, CancellationToken.None));
    }

    public void Dispose() => _dir.Dispose();
}

/// <summary>`scrape --revalidate`: PDFs already on disk are checked with the validators stored in the manifest.</summary>
public class ScrapeRevalidationTests : IDisposable
{
    private const string PdfPath = "/legislacion/superin/2025/000401-2025.pdf";

    private readonly TempDir _dir = new();
    private readonly List<HttpRequestMessage> _seen = [];
    private readonly ScrapeRunner _runner;
    private readonly ManifestStore _manifest;
    private readonly DataPaths _paths;

    // What the "site" serves for the PDF; each test sets the scenario.
    private Func<HttpRequestMessage, HttpResponseMessage> _pdf = _ => Responses.Pdf("v1");

    public ScrapeRevalidationTests()
    {
        _paths = new DataPaths(_dir.Path);
        var options = Options.Create(new ScrapingOptions { BaseUrl = "https://sunat.test/", RetryBaseDelay = TimeSpan.FromMilliseconds(1) });
        var site = new StubHttpHandler(request =>
        {
            _seen.Add(request);
            return request.RequestUri!.AbsolutePath switch
            {
                "/legislacion/general/index.html" => Responses.Html("""<a href="/legislacion/superin/2025/indices/indcor.htm">2025</a>"""),
                "/legislacion/tributaria/index.html" => Responses.Html("<html></html>"),
                "/legislacion/superin/2025/indices/indcor.htm" => Responses.Html(
                    """<table><tr><td><a href="../000401-2025.pdf">R. Superintendencia N° 000401-2025</a></td><td>Aprueba algo</td><td>31/12/2025</td></tr></table>"""),
                PdfPath => _pdf(request),
                _ => Responses.Status(HttpStatusCode.NotFound)
            };
        });
        var http = new SunatHttpClient(new HttpClient(site), options, NullLogger<SunatHttpClient>.Instance);
        _manifest = new ManifestStore(_paths, NullLogger<ManifestStore>.Instance);
        _runner = new ScrapeRunner(
            new IndexPageCache(_paths, http, options, TimeProvider.System, NullLogger<IndexPageCache>.Instance),
            new PdfDownloader(http, new PdfStore(_paths)),
            _manifest, _paths, options, TimeProvider.System, NullLogger<ScrapeRunner>.Instance);
    }

    private static HttpResponseMessage PdfWithEtag(string body, string etag)
    {
        var response = Responses.Pdf(body);
        response.Headers.ETag = new EntityTagHeaderValue(etag);
        return response;
    }

    private static ScrapeRequest Request(bool revalidate) =>
        new(FromYear: 2025, ToYear: 2025, Limit: null, IncludeAnexos: false, RefreshIndex: false, Revalidate: revalidate);

    private int PdfRequests => _seen.Count(r => r.RequestUri!.AbsolutePath == PdfPath);

    [Fact]
    public async Task IndexOnly_lists_the_normas_and_downloads_nothing()
    {
        var summary = await _runner.RunAsync(
            new ScrapeRequest(2025, 2025, Limit: null, IncludeAnexos: false, RefreshIndex: false, IndexOnly: true), CancellationToken.None);

        Assert.Equal(1, summary.Listed);
        Assert.Equal("000401-2025", Assert.Single(summary.Listings).Numero);
        Assert.Equal(0, summary.Downloaded);
        Assert.Equal(0, PdfRequests);
        Assert.False(File.Exists(_paths.Manifest)); // nothing was recorded either
    }

    [Fact]
    public async Task Without_the_flag_existing_pdfs_cost_no_request()
    {
        _pdf = _ => PdfWithEtag("v1", "\"p1\"");
        await _runner.RunAsync(Request(revalidate: false), CancellationToken.None);

        var summary = await _runner.RunAsync(Request(revalidate: false), CancellationToken.None);

        Assert.Equal(1, PdfRequests);
        Assert.Equal(0, summary.Revalidated);
    }

    [Fact]
    public async Task A_304_counts_as_unchanged_and_the_request_carries_the_manifests_etag()
    {
        _pdf = r => r.Headers.IfNoneMatch.Any(t => t.Tag == "\"p1\"") ? Responses.Status(HttpStatusCode.NotModified) : PdfWithEtag("v1", "\"p1\"");
        await _runner.RunAsync(Request(revalidate: false), CancellationToken.None);

        var summary = await _runner.RunAsync(Request(revalidate: true), CancellationToken.None);

        Assert.Equal(1, summary.AlreadyDownloaded);
        Assert.Equal(1, summary.Revalidated);
        Assert.Equal(1, summary.Unchanged);
        Assert.Equal(0, summary.Updated);
        Assert.Empty(summary.Failures);
        Assert.Contains(_seen.Last(r => r.RequestUri!.AbsolutePath == PdfPath).Headers.IfNoneMatch, t => t.Tag == "\"p1\"");
    }

    [Fact]
    public async Task A_changed_pdf_gets_a_new_manifest_line_with_the_new_hash_and_validator()
    {
        var published = "v1";
        _pdf = _ => PdfWithEtag(published, $"\"{published}\"");
        await _runner.RunAsync(Request(revalidate: false), CancellationToken.None);
        var before = (await _manifest.LoadAsync(CancellationToken.None)).Values.Single();

        published = "v2"; // the site replaced the PDF
        var summary = await _runner.RunAsync(Request(revalidate: true), CancellationToken.None);

        var after = (await _manifest.LoadAsync(CancellationToken.None)).Values.Single(); // last line for the Id wins
        Assert.Equal(1, summary.Updated);
        Assert.Equal(0, summary.Unchanged);
        Assert.NotEqual(before.Sha256, after.Sha256);
        Assert.Equal("\"v2\"", after.ETag);
        Assert.Equal(2, File.ReadAllLines(_paths.Manifest).Count(l => l.Length > 0)); // append-only: history is kept
    }

    [Fact]
    public async Task A_server_that_ignores_validators_is_compared_by_hash_and_writes_nothing()
    {
        _pdf = _ => PdfWithEtag("v1", "\"p1\""); // always 200 with the same bytes
        await _runner.RunAsync(Request(revalidate: false), CancellationToken.None);

        var summary = await _runner.RunAsync(Request(revalidate: true), CancellationToken.None);

        Assert.Equal(1, summary.Unchanged);
        Assert.Equal(0, summary.Updated);
        Assert.Single(File.ReadAllLines(_paths.Manifest), l => l.Length > 0);
    }

    [Fact]
    public async Task Documents_saved_without_validators_are_reported_not_downloaded_again()
    {
        _pdf = _ => Responses.Pdf("v1"); // no ETag, no Last-Modified
        await _runner.RunAsync(Request(revalidate: false), CancellationToken.None);

        var summary = await _runner.RunAsync(Request(revalidate: true), CancellationToken.None);

        Assert.Equal(1, summary.NotRevalidable);
        Assert.Equal(0, summary.Revalidated);
        Assert.Equal(1, PdfRequests); // only the original download
    }

    public void Dispose() => _dir.Dispose();
}
