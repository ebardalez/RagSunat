using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SunatRAG.Core.Documents;
using SunatRAG.Ingestion.Scraping;

namespace SunatRAG.Tests.Scraping;

public class PdfStoreTests
{
    private static MemoryStream Bytes(string text) => new(Encoding.ASCII.GetBytes(text));

    [Fact]
    public async Task Saves_content_addressed_and_detects_identical_content()
    {
        using var dir = new TempDir();
        var store = new PdfStore(new DataPaths(dir.Path));
        var expectedHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.ASCII.GetBytes("%PDF-1.4 hola")));

        var first = await store.SaveAsync(Bytes("%PDF-1.4 hola"), CancellationToken.None);
        var second = await store.SaveAsync(Bytes("%PDF-1.4 hola"), CancellationToken.None);

        Assert.Equal(expectedHash, first.Sha256);
        Assert.Equal($"pdfs/{expectedHash[..2]}/{expectedHash}.pdf", first.RelativePath);
        Assert.False(first.IsDuplicate);
        Assert.True(second.IsDuplicate);
        Assert.True(File.Exists(Path.Combine(dir.Path, first.RelativePath)));
        Assert.Single(Directory.GetFiles(Path.Combine(dir.Path, "pdfs"), "*.pdf", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Rejects_non_pdf_bodies_and_leaves_no_files_behind()
    {
        using var dir = new TempDir();
        var store = new PdfStore(new DataPaths(dir.Path));

        await Assert.ThrowsAsync<InvalidDataException>(
            () => store.SaveAsync(Bytes("<html>Error 200 disfrazado</html>"), CancellationToken.None));

        Assert.Empty(Directory.GetFiles(dir.Path, "*", SearchOption.AllDirectories));
    }
}

public class ManifestStoreTests
{
    private static NormaDocument Doc(string id) => new(
        Id: id,
        Role: DocumentRole.Principal,
        ParentId: null,
        AnexoLabel: null,
        Norma: new NormaListing(
            id, TipoNorma.ResolucionSuperintendencia, 2025, "000399-2025",
            "Resolución de Superintendencia N° 000399-2025", "Modifican el Reglamento",
            new DateOnly(2025, 12, 31), null, new Uri($"https://sunat.test/{id}.pdf"),
            [new AnexoLink("Anexo 1", new Uri($"https://sunat.test/anexo-{id}.pdf"))]),
        Url: new Uri($"https://sunat.test/{id}.pdf"),
        Sha256: "ab12",
        RelativePath: "pdfs/ab/ab12.pdf",
        SizeBytes: 1234,
        DownloadedAtUtc: new DateTimeOffset(2026, 10, 28, 12, 0, 0, TimeSpan.Zero),
        ETag: "\"abc\"",
        LastModified: null);

    [Fact]
    public async Task Round_trips_records_including_enums_dates_and_uris()
    {
        using var dir = new TempDir();
        var store = new ManifestStore(new DataPaths(dir.Path), NullLogger<ManifestStore>.Instance);

        await store.AppendAsync(Doc("a"), CancellationToken.None);
        var loaded = await store.LoadAsync(CancellationToken.None);

        var a = loaded["a"];
        Assert.Equal(TipoNorma.ResolucionSuperintendencia, a.Norma.Tipo);
        Assert.Equal(new DateOnly(2025, 12, 31), a.Norma.FechaPublicacion);
        Assert.Equal("https://sunat.test/a.pdf", a.Url.AbsoluteUri);
        Assert.Equal("Anexo 1", Assert.Single(a.Norma.Anexos).Label);
        Assert.Equal(1234, a.SizeBytes);
    }

    [Fact]
    public async Task A_half_written_last_line_is_skipped_and_does_not_corrupt_the_next_append()
    {
        using var dir = new TempDir();
        var paths = new DataPaths(dir.Path);
        var store = new ManifestStore(paths, NullLogger<ManifestStore>.Instance);
        await store.AppendAsync(Doc("a"), CancellationToken.None);
        await File.AppendAllTextAsync(paths.Manifest, "{\"id\":\"b\",\"role\":", CancellationToken.None); // crash mid-write

        await store.AppendAsync(Doc("c"), CancellationToken.None);
        var loaded = await store.LoadAsync(CancellationToken.None);

        Assert.Equal(["a", "c"], loaded.Keys.Order());
    }

    [Fact]
    public async Task Last_line_for_an_id_wins()
    {
        using var dir = new TempDir();
        var store = new ManifestStore(new DataPaths(dir.Path), NullLogger<ManifestStore>.Instance);

        await store.AppendAsync(Doc("a"), CancellationToken.None);
        await store.AppendAsync(Doc("a") with { SizeBytes = 999 }, CancellationToken.None);

        Assert.Equal(999, (await store.LoadAsync(CancellationToken.None))["a"].SizeBytes);
    }
}

public class ScrapeRunnerTests
{
    private const string GeneralSeed = """
        <a href="/legislacion/superin/2025/indices/indcor.htm">2025</a>
        <a href="/legislacion/superin/2016/indices/indcor.htm">2016</a>
        """;

    private const string TributariaSeed = """
        <a href="/legislacion/oficios/2025/indcor.htm">2025</a>
        """;

    private const string Superin2025 = """
        <table>
          <tr>
            <td><a href="../000401-2025.pdf">R. Superintendencia N° 000401-2025</a></td>
            <td>Aprueba indicadores - <a href="../anexo1-000401-2025.pdf">Anexo 1</a> - <a href="../anexo2-000401-2025.pdf">Anexo 2</a></td>
            <td>&nbsp;</td>
          </tr>
          <tr>
            <td><a href="../000399-2025.pdf">R. Superintendencia N° 000399-2025</a></td>
            <td>Modifican el Reglamento</td>
            <td>31/12/2025</td>
          </tr>
        </table>
        """;

    private const string Oficios2025 = """
        <table>
          <tr>
            <td><a href="informe-oficios/i000131-2025-7T0000.pdf">Informe N° 0000131-2025-SUNAT/7T0000</a></td>
            <td>Compensación del saldo a favor</td>
            <td>08/01/2026</td>
          </tr>
        </table>
        """;

    private sealed class Rig : IDisposable
    {
        private readonly TempDir _dir = new();
        private readonly Func<string, HttpResponseMessage?> _override;

        public StubHttpHandler Site { get; }
        public ScrapeRunner Runner { get; }
        public DataPaths Paths { get; }
        public ManifestStore Manifest { get; }

        public Rig(Func<string, HttpResponseMessage?>? overridePdf = null)
        {
            _override = overridePdf ?? (_ => null);
            Paths = new DataPaths(_dir.Path);
            var options = Options.Create(new ScrapingOptions { BaseUrl = "https://sunat.test/", RetryBaseDelay = TimeSpan.FromMilliseconds(1) });

            Site = new StubHttpHandler(Route);
            var http = new SunatHttpClient(
                new HttpClient(Site) { BaseAddress = new Uri("https://sunat.test/") }, options, NullLogger<SunatHttpClient>.Instance);

            Manifest = new ManifestStore(Paths, NullLogger<ManifestStore>.Instance);
            Runner = new ScrapeRunner(
                new IndexPageCache(Paths, http, options, TimeProvider.System, NullLogger<IndexPageCache>.Instance),
                new PdfDownloader(http, new PdfStore(Paths)),
                Manifest, Paths, options, TimeProvider.System, NullLogger<ScrapeRunner>.Instance);
        }

        private HttpResponseMessage Route(HttpRequestMessage request)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) && _override(path) is { } custom) return custom;

            return path switch
            {
                "/legislacion/general/index.html" => Responses.Html(GeneralSeed),
                "/legislacion/tributaria/index.html" => Responses.Html(TributariaSeed),
                "/legislacion/superin/2025/indices/indcor.htm" => Responses.Html(Superin2025),
                "/legislacion/oficios/2025/indcor.htm" => Responses.Html(Oficios2025),
                // Resolución 399 and the informe deliberately serve identical bytes -> duplicate content.
                "/legislacion/superin/2025/000399-2025.pdf" or
                "/legislacion/oficios/2025/informe-oficios/i000131-2025-7T0000.pdf" => Responses.Pdf("contenido compartido"),
                _ when path.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) => Responses.Pdf(path),
                _ => Responses.Status(HttpStatusCode.NotFound)
            };
        }

        public int PdfRequests => Site.Requests.Count(u => u.AbsolutePath.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase));

        public void Dispose() => _dir.Dispose();
    }

    private static ScrapeRequest Request(int? limit = null, bool anexos = false) =>
        new(FromYear: 2025, ToYear: 2025, Limit: limit, IncludeAnexos: anexos, RefreshIndex: false);

    [Fact]
    public async Task First_run_discovers_filters_by_year_downloads_and_dedupes_by_hash()
    {
        using var rig = new Rig();

        var summary = await rig.Runner.RunAsync(Request(), CancellationToken.None);

        Assert.Equal(2, summary.IndexPages); // 2016 is outside the range
        Assert.Equal(3, summary.Listed);
        Assert.Equal(3, summary.Downloaded);
        Assert.Equal(1, summary.DuplicateContent);
        Assert.Empty(summary.Failures);
        Assert.Equal(3, (await rig.Manifest.LoadAsync(CancellationToken.None)).Count);
        Assert.Equal(2, Directory.GetFiles(Path.Combine(rig.Paths.Root, "pdfs"), "*.pdf", SearchOption.AllDirectories).Length);
    }

    [Fact]
    public async Task Second_run_is_a_no_op_for_pdfs_and_uses_the_cached_indexes()
    {
        using var rig = new Rig();
        await rig.Runner.RunAsync(Request(), CancellationToken.None);
        var requestsAfterFirstRun = rig.Site.Requests.Count;

        var summary = await rig.Runner.RunAsync(Request(), CancellationToken.None);

        Assert.Equal(0, summary.Downloaded);
        Assert.Equal(3, summary.AlreadyDownloaded);
        Assert.Equal(requestsAfterFirstRun, rig.Site.Requests.Count); // not even index pages were requested again
    }

    [Fact]
    public async Task Anexos_are_downloaded_only_when_asked_and_are_linked_to_their_parent()
    {
        using var rig = new Rig();
        await rig.Runner.RunAsync(Request(), CancellationToken.None);

        var summary = await rig.Runner.RunAsync(Request(anexos: true), CancellationToken.None);
        var manifest = await rig.Manifest.LoadAsync(CancellationToken.None);

        Assert.Equal(2, summary.Downloaded);
        var anexo = manifest["superin-2025-anexo1-000401-2025"];
        Assert.Equal(DocumentRole.Anexo, anexo.Role);
        Assert.Equal("superin-2025-000401-2025", anexo.ParentId);
        Assert.Equal("Anexo 1", anexo.AnexoLabel);
        Assert.Equal("000401-2025", anexo.Norma.Numero); // the chunker can cite the parent norma
    }

    [Fact]
    public async Task Limit_stops_early_and_the_next_run_continues_where_it_left_off()
    {
        using var rig = new Rig();

        var first = await rig.Runner.RunAsync(Request(limit: 1), CancellationToken.None);
        var second = await rig.Runner.RunAsync(Request(), CancellationToken.None);

        Assert.True(first.LimitReached);
        Assert.Equal(1, first.Downloaded);
        Assert.Equal(2, second.Downloaded);
        Assert.Equal(1, second.AlreadyDownloaded);
    }

    [Fact]
    public async Task A_failing_pdf_is_reported_and_does_not_stop_the_run()
    {
        using var rig = new Rig(path => path.EndsWith("000399-2025.pdf")
            ? Responses.Status(HttpStatusCode.NotFound)
            : null);

        var summary = await rig.Runner.RunAsync(Request(), CancellationToken.None);

        Assert.Equal(2, summary.Downloaded);
        var failure = Assert.Single(summary.Failures);
        Assert.EndsWith("000399-2025.pdf", failure.Url.AbsolutePath);
    }

    [Fact]
    public async Task An_html_error_page_served_as_200_is_rejected_and_not_recorded()
    {
        using var rig = new Rig(path => path.EndsWith("000401-2025.pdf")
            ? Responses.Html("<html>Servicio no disponible</html>")
            : null);

        var summary = await rig.Runner.RunAsync(Request(), CancellationToken.None);

        Assert.Single(summary.Failures);
        Assert.DoesNotContain("superin-2025-000401-2025", (await rig.Manifest.LoadAsync(CancellationToken.None)).Keys);
    }

    [Fact]
    public async Task Http_429_stops_the_whole_run_and_keeps_what_was_already_downloaded()
    {
        // Needs the polite handler in the chain, because that is what turns 429 into RateLimitedException.
        using var dir = new TempDir();
        var paths = new DataPaths(dir.Path);
        var options = Options.Create(new ScrapingOptions { BaseUrl = "https://sunat.test/", RetryBaseDelay = TimeSpan.FromMilliseconds(1) });
        var throttle = new RequestThrottle(TimeSpan.Zero);
        var robots = new RobotsPolicy(
            new HttpClient(new StubHttpHandler(_ => Responses.Status(HttpStatusCode.NotFound))) { BaseAddress = new Uri("https://sunat.test/") },
            throttle, "SunatRAG", NullLogger<RobotsPolicy>.Instance);

        var pdfCalls = 0;
        var site = new StubHttpHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/legislacion/general/index.html" => Responses.Html(GeneralSeed),
            "/legislacion/tributaria/index.html" => Responses.Html(TributariaSeed),
            "/legislacion/superin/2025/indices/indcor.htm" => Responses.Html(Superin2025),
            "/legislacion/oficios/2025/indcor.htm" => Responses.Html(Oficios2025),
            _ => ++pdfCalls == 1 ? Responses.Pdf("primero") : Responses.Status(HttpStatusCode.TooManyRequests)
        });
        var http = new SunatHttpClient(
            new HttpClient(new PoliteHttpHandler(throttle, robots) { InnerHandler = site }) { BaseAddress = new Uri("https://sunat.test/") },
            options, NullLogger<SunatHttpClient>.Instance);
        var manifest = new ManifestStore(paths, NullLogger<ManifestStore>.Instance);
        var runner = new ScrapeRunner(
            new IndexPageCache(paths, http, options, TimeProvider.System, NullLogger<IndexPageCache>.Instance),
            new PdfDownloader(http, new PdfStore(paths)),
            manifest, paths, options, TimeProvider.System, NullLogger<ScrapeRunner>.Instance);

        var summary = await runner.RunAsync(Request(), CancellationToken.None);

        Assert.NotNull(summary.RateLimit);
        Assert.Equal(1, summary.Downloaded);
        Assert.Equal(2, pdfCalls); // the 429 was not retried and no further PDF was attempted
        Assert.Single(await manifest.LoadAsync(CancellationToken.None));
    }
}
