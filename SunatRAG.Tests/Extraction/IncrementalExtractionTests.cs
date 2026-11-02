using Microsoft.Extensions.Logging.Abstractions;
using SunatRAG.Core.Documents;
using SunatRAG.Ingestion.Extraction;
using SunatRAG.Ingestion.Scraping;
using SunatRAG.Tests.Scraping;

namespace SunatRAG.Tests.Extraction;

public class IncrementalExtractionTests
{
    private const string Body = "CONSIDERANDO:\nQue, la presente resolucion aprueba el procedimiento general de prueba.\nSE RESUELVE:\nArticulo 1.- Objeto de la norma.";

    private static async Task AddAsync(TempDir dir, ManifestStore store, string id)
    {
        var relative = $"pdfs/{id}.pdf";
        var full = Path.Combine(dir.Path, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var pdf = PdfFactory.Build(Body);
        await File.WriteAllBytesAsync(full, pdf);

        var url = new Uri($"https://www.sunat.gob.pe/legislacion/superin/2025/{id}.pdf");
        var listing = new NormaListing(id, TipoNorma.ResolucionSuperintendencia, 2025, "000001-2025", $"Resolución N° {id}",
            "Aprueba el procedimiento general de prueba", new DateOnly(2025, 1, 2), null, url, []);
        await store.AppendAsync(new NormaDocument(id, DocumentRole.Principal, null, null, listing, url, "sha-" + id, relative,
            pdf.Length, DateTimeOffset.UtcNow, null, null), CancellationToken.None);
    }

    private static (ChunkingRunner Runner, ManifestStore Store, TokenAwareChunker Chunker) Arrange(TempDir dir)
    {
        var paths = new DataPaths(dir.Path);
        var store = new ManifestStore(paths, NullLogger<ManifestStore>.Instance);
        var runner = new ChunkingRunner(paths, store, new PdfTextExtractor(), NullLogger<ChunkingRunner>.Instance);
        return (runner, store, new TokenAwareChunker(new ApproximateTokenCounter(), new ChunkingOptions()));
    }

    private static ChunkingRequest Incremental(string fingerprint = "f1") => new(false, null, null, true, fingerprint);

    [Fact]
    public void Plan_separates_reusable_new_changed_and_removed()
    {
        var previous = new Dictionary<string, string> { ["same"] = "k1", ["changed"] = "k1", ["gone"] = "k1" };

        var plan = ChunkingRunner.Plan([("same", "k1"), ("changed", "k2"), ("fresh", "k1")], previous);

        Assert.Equal(["same"], plan.Reusable);
        Assert.Equal(1, plan.New);
        Assert.Equal(1, plan.Changed);
        Assert.Equal(1, plan.Removed);
    }

    [Fact]
    public async Task Fingerprint_changes_with_the_pdf_hash_the_metadata_and_the_recipe()
    {
        using var dir = new TempDir();
        var (_, store, _) = Arrange(dir);
        await AddAsync(dir, store, "a");
        var doc = (await store.LoadAsync(CancellationToken.None))["a"];

        var baseKey = ChunkingRunner.Fingerprint(doc, "r1");

        Assert.Equal(baseKey, ChunkingRunner.Fingerprint(doc, "r1"));
        Assert.NotEqual(baseKey, ChunkingRunner.Fingerprint(doc, "r2"));
        Assert.NotEqual(baseKey, ChunkingRunner.Fingerprint(doc with { Sha256 = "other" }, "r1"));
        Assert.NotEqual(baseKey, ChunkingRunner.Fingerprint(doc with { Norma = doc.Norma with { Sumilla = "otra sumilla" } }, "r1"));
    }

    [Fact]
    public async Task Without_previous_files_an_incremental_run_is_a_full_run_that_writes_the_state()
    {
        using var dir = new TempDir();
        var (runner, store, chunker) = Arrange(dir);
        await AddAsync(dir, store, "a");

        var summary = await runner.RunAsync(chunker, Incremental(), CancellationToken.None);

        Assert.Null(summary.Incremental);
        Assert.NotEmpty(summary.Chunks);
        Assert.True(File.Exists(runner.StateFile));
    }

    [Fact]
    public async Task Unchanged_documents_are_reused_without_touching_their_pdf()
    {
        using var dir = new TempDir();
        var (runner, store, chunker) = Arrange(dir);
        await AddAsync(dir, store, "a");
        await AddAsync(dir, store, "b");
        var first = await runner.RunAsync(chunker, Incremental(), CancellationToken.None);

        // Proof of reuse: if the runner re-read the PDF it would now fail; reused chunks come from chunks.jsonl.
        File.Delete(Path.Combine(dir.Path, "pdfs", "a.pdf"));
        var second = await runner.RunAsync(chunker, Incremental(), CancellationToken.None);

        Assert.Equal(new IncrementalStats(2, 0, 0, 0), second.Incremental);
        Assert.Equal(first.Chunks.Select(c => (c.ChunkId, c.TextHash)), second.Chunks.Select(c => (c.ChunkId, c.TextHash)));
        Assert.All(second.Reports, r => Assert.Equal("Ok", r.Status));
    }

    [Fact]
    public async Task A_new_document_is_processed_and_the_rest_reused()
    {
        using var dir = new TempDir();
        var (runner, store, chunker) = Arrange(dir);
        await AddAsync(dir, store, "a");
        await runner.RunAsync(chunker, Incremental(), CancellationToken.None);

        await AddAsync(dir, store, "b");
        var second = await runner.RunAsync(chunker, Incremental(), CancellationToken.None);

        Assert.Equal(new IncrementalStats(1, 0, 1, 0), second.Incremental);
        Assert.Equal(["a", "b"], second.Chunks.Select(c => c.DocumentId).Distinct());
    }

    [Fact]
    public async Task A_different_chunking_recipe_invalidates_every_document()
    {
        using var dir = new TempDir();
        var (runner, store, chunker) = Arrange(dir);
        await AddAsync(dir, store, "a");
        await runner.RunAsync(chunker, Incremental("target=500"), CancellationToken.None);

        var second = await runner.RunAsync(chunker, Incremental("target=300"), CancellationToken.None);

        Assert.Equal(new IncrementalStats(0, 1, 0, 0), second.Incremental);
    }

    [Fact]
    public async Task Documents_gone_from_the_manifest_are_dropped_from_the_output()
    {
        using var dir = new TempDir();
        var (runner, store, chunker) = Arrange(dir);
        await AddAsync(dir, store, "a");
        await AddAsync(dir, store, "b");
        await runner.RunAsync(chunker, Incremental(), CancellationToken.None);

        // Simulate removal by rewriting the manifest without "b".
        var manifestFile = Directory.GetFiles(dir.Path, "manifest.jsonl", SearchOption.AllDirectories).Single();
        var lines = (await File.ReadAllLinesAsync(manifestFile)).Where(l => !l.Contains("\"b\"")).ToArray();
        await File.WriteAllLinesAsync(manifestFile, lines);

        var second = await runner.RunAsync(chunker, Incremental(), CancellationToken.None);

        Assert.Equal(1, second.Incremental!.Removed);
        Assert.DoesNotContain(second.Chunks, c => c.DocumentId == "b");
        Assert.DoesNotContain(second.Reports, r => r.Id == "b");
    }
}
