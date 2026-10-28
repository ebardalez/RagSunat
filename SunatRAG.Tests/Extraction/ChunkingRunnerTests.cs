using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using SunatRAG.Core.Documents;
using SunatRAG.Ingestion.Extraction;
using SunatRAG.Ingestion.Scraping;
using SunatRAG.Tests.Scraping;

namespace SunatRAG.Tests.Extraction;

public class ChunkingRunnerTests
{
    private static readonly string Body = string.Join("\n",
        "CONSIDERANDO:",
        "Que, la presente resolucion aprueba el procedimiento general de prueba.",
        "SE RESUELVE:",
        "Articulo 1.- Objeto de la norma aprobada en la presente resolucion.");

    private static async Task<NormaDocument> AddAsync(
        TempDir dir, ManifestStore store, string id, TipoNorma tipo, string sumilla, byte[] pdf)
    {
        var relative = $"pdfs/{id}.pdf";
        var full = Path.Combine(dir.Path, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        await File.WriteAllBytesAsync(full, pdf);

        var url = new Uri($"https://www.sunat.gob.pe/legislacion/superin/2025/{id}.pdf");
        var listing = new NormaListing(id, tipo, 2025, "000001-2025", $"Resolución de Superintendencia N° {id}",
            sumilla, new DateOnly(2025, 1, 2), null, url, []);
        var doc = new NormaDocument(id, DocumentRole.Principal, null, null, listing, url, "sha", relative,
            pdf.Length, DateTimeOffset.UtcNow, null, null);
        await store.AppendAsync(doc, CancellationToken.None);
        return doc;
    }

    private static async Task<(ChunkingRunner Runner, TokenAwareChunker Chunker, DataPaths Paths)> ArrangeAsync(TempDir dir)
    {
        var paths = new DataPaths(dir.Path);
        var store = new ManifestStore(paths, NullLogger<ManifestStore>.Instance);

        await AddAsync(dir, store, "norma-ok", TipoNorma.ResolucionSuperintendencia,
            "Aprueba el procedimiento general de prueba", PdfFactory.Build(Body));
        await AddAsync(dir, store, "norma-admin", TipoNorma.ResolucionSuperintendencia,
            "Designa en cargo de confianza", PdfFactory.Build(Body));
        await AddAsync(dir, store, "norma-escaneada", TipoNorma.ResolucionSuperintendencia,
            "Modifica el reglamento", PdfFactory.Build(null, null));
        await AddAsync(dir, store, "informe-ok", TipoNorma.Informe,
            "Criterio sobre la compensacion", PdfFactory.Build("MATERIA:\nSe consulta si procede la compensacion del saldo a favor."));

        var runner = new ChunkingRunner(paths, store, new PdfTextExtractor(), NullLogger<ChunkingRunner>.Instance);
        var chunker = new TokenAwareChunker(new ApproximateTokenCounter(), new ChunkingOptions());
        return (runner, chunker, paths);
    }

    [Fact]
    public async Task Every_document_gets_a_report_line_with_the_reason_it_was_or_was_not_chunked()
    {
        using var dir = new TempDir();
        var (runner, chunker, _) = await ArrangeAsync(dir);

        var summary = await runner.RunAsync(chunker, new ChunkingRequest(false, null, null), CancellationToken.None);

        var status = summary.Reports.ToDictionary(r => r.Id, r => r.Status);
        Assert.Equal("Ok", status["norma-ok"]);
        Assert.Equal("SkippedAdministrativa", status["norma-admin"]);
        Assert.Equal("NoText", status["norma-escaneada"]);
        Assert.Equal("Ok", status["informe-ok"]);
        Assert.Equal("personal:designacion", summary.Reports.Single(r => r.Id == "norma-admin").CategoriaRule);
    }

    [Fact]
    public async Task Chunks_carry_the_metadata_needed_for_citation()
    {
        using var dir = new TempDir();
        var (runner, chunker, _) = await ArrangeAsync(dir);

        var summary = await runner.RunAsync(chunker, new ChunkingRequest(false, null, null), CancellationToken.None);

        var chunk = summary.Chunks.First(c => c.DocumentId == "norma-ok");
        Assert.Equal("norma-ok#0000", chunk.ChunkId);
        Assert.Equal(1, chunk.PageStart);
        Assert.Equal(TipoNorma.ResolucionSuperintendencia, chunk.Tipo);
        Assert.Equal(Categoria.Normativa, chunk.Categoria);
        Assert.Equal("https://www.sunat.gob.pe/legislacion/superin/2025/norma-ok.pdf", chunk.Url.ToString());
        Assert.Equal(16, chunk.TextHash.Length);
        Assert.Contains("pág. 1", chunk.Cita());
        Assert.DoesNotContain(summary.Chunks, c => c.DocumentId is "norma-admin" or "norma-escaneada");
    }

    [Fact]
    public async Task Including_administrativas_chunks_them_too()
    {
        using var dir = new TempDir();
        var (runner, chunker, _) = await ArrangeAsync(dir);

        var summary = await runner.RunAsync(chunker, new ChunkingRequest(true, null, null), CancellationToken.None);

        Assert.Contains(summary.Chunks, c => c.DocumentId == "norma-admin" && c.Categoria == Categoria.Administrativa);
    }

    [Fact]
    public async Task Output_files_are_written_and_the_run_is_deterministic()
    {
        using var dir = new TempDir();
        var (runner, chunker, _) = await ArrangeAsync(dir);

        var first = await runner.RunAsync(chunker, new ChunkingRequest(false, null, null), CancellationToken.None);
        var written = await File.ReadAllLinesAsync(runner.ChunksFile);
        var second = await runner.RunAsync(chunker, new ChunkingRequest(false, null, null), CancellationToken.None);

        Assert.True(first.WroteFiles);
        Assert.Equal(first.Chunks.Count, written.Length);
        Assert.Equal(first.Chunks.Select(c => (c.ChunkId, c.TextHash)), second.Chunks.Select(c => (c.ChunkId, c.TextHash)));
        Assert.False(File.Exists(runner.ChunksFile + ".tmp"));

        var roundTrip = JsonSerializer.Deserialize<DocumentChunk>(written[0], ManifestStore.Json);
        Assert.Equal(first.Chunks[0], roundTrip);
    }

    [Fact]
    public async Task Partial_runs_do_not_overwrite_the_output_files()
    {
        using var dir = new TempDir();
        var (runner, chunker, _) = await ArrangeAsync(dir);

        var summary = await runner.RunAsync(chunker, new ChunkingRequest(false, null, "norma-ok"), CancellationToken.None);

        Assert.False(summary.WroteFiles);
        Assert.False(File.Exists(runner.ChunksFile));
        Assert.All(summary.Chunks, c => Assert.Equal("norma-ok", c.DocumentId));
    }
}
