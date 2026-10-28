using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SunatRAG.Core.Documents;
using SunatRAG.Ingestion.Scraping;

namespace SunatRAG.Ingestion.Extraction;

/// <param name="IncludeAdministrativas">Default false: administrative resolutions are not indexed.</param>
/// <param name="OnlyId">Debug: process a single document and write nothing to disk.</param>
public sealed record ChunkingRequest(bool IncludeAdministrativas, int? Limit, string? OnlyId);

/// <summary>One line of data/extraction-report.jsonl: why each document ended up the way it did.</summary>
public sealed record DocumentReport(
    string Id,
    string Titulo,
    Categoria Categoria,
    string CategoriaRule,
    string Status,
    int Pages,
    int EmptyPages,
    int Chunks,
    int Tokens,
    string? Detail);

public sealed record ChunkingSummary(
    IReadOnlyList<DocumentReport> Reports,
    IReadOnlyList<DocumentChunk> Chunks,
    bool WroteFiles);

/// <summary>
/// manifest.jsonl -> (PdfPig -> normalize -> split -> chunk) -> chunks.jsonl + extraction-report.jsonl.
/// Deliberately NOT incremental: the whole step takes seconds and is deterministic, so re-running it is cheaper than
/// maintaining a cache. Resumability lives in Phase 3, where the slow step (embedding) is, keyed by ChunkId + TextHash.
/// </summary>
public sealed class ChunkingRunner(DataPaths paths, ManifestStore manifestStore, PdfTextExtractor extractor, ILogger<ChunkingRunner> logger)
{
    public string ChunksFile => Path.Combine(paths.Root, "chunks.jsonl");
    public string ReportFile => Path.Combine(paths.Root, "extraction-report.jsonl");

    public async Task<ChunkingSummary> RunAsync(TokenAwareChunker chunker, ChunkingRequest request, CancellationToken ct)
    {
        var manifest = await manifestStore.LoadAsync(ct);
        IEnumerable<NormaDocument> docs = manifest.Values.OrderBy(d => d.Id, StringComparer.Ordinal);
        if (request.OnlyId is not null) docs = docs.Where(d => d.Id == request.OnlyId);

        var reports = new List<DocumentReport>();
        var allChunks = new List<DocumentChunk>();
        var processed = 0;

        foreach (var doc in docs)
        {
            ct.ThrowIfCancellationRequested();
            if (request.Limit is { } limit && processed >= limit) break;
            processed++;

            var (categoria, rule) = CategoriaClassifier.Classify(doc.Norma.Tipo, doc.Norma.Sumilla);
            if (categoria == Categoria.Administrativa && !request.IncludeAdministrativas)
            {
                reports.Add(Report(doc, categoria, rule, "SkippedAdministrativa", 0, 0, 0, 0, null));
                continue;
            }

            var extraction = extractor.Extract(Path.Combine(paths.Root, doc.RelativePath), ct);
            if (extraction.Status != ExtractionStatus.Ok)
            {
                logger.LogWarning("{Id}: {Status} {Error}", doc.Id, extraction.Status, extraction.Error);
                reports.Add(Report(doc, categoria, rule, extraction.Status.ToString(),
                    extraction.PageCount, extraction.EmptyPages, 0, 0, extraction.Error));
                continue;
            }

            var paragraphs = TextNormalizer.ToParagraphs(extraction.Pages);
            var sections = LegalSectionSplitter.Split(paragraphs);
            var chunks = chunker.Chunk(sections);

            var docChunks = chunks.Select((c, i) => ToDocumentChunk(doc, categoria, c, i)).ToList();
            allChunks.AddRange(docChunks);
            reports.Add(Report(doc, categoria, rule, "Ok", extraction.PageCount, extraction.EmptyPages,
                docChunks.Count, docChunks.Sum(c => c.TokenCount), null));
        }

        var write = request.OnlyId is null && request.Limit is null;
        if (write) await WriteAsync(reports, allChunks, ct);
        return new ChunkingSummary(reports, allChunks, write);
    }

    private static DocumentChunk ToDocumentChunk(NormaDocument doc, Categoria categoria, TextChunk c, int index) => new(
        ChunkId: $"{doc.Id}#{index:D4}",
        DocumentId: doc.Id,
        Index: index,
        Text: c.Text,
        TokenCount: c.TokenCount,
        TextHash: Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(c.Text)))[..16],
        PageStart: c.PageStart,
        PageEnd: c.PageEnd,
        Heading: c.Heading,
        Titulo: doc.Norma.Titulo,
        Tipo: doc.Norma.Tipo,
        Numero: doc.Norma.Numero,
        Sumilla: doc.Norma.Sumilla,
        FechaPublicacion: doc.Norma.FechaPublicacion,
        FechaVigencia: doc.Norma.FechaVigencia,
        Url: doc.Url,
        Categoria: categoria);

    private static DocumentReport Report(NormaDocument doc, Categoria categoria, string rule, string status,
        int pages, int empty, int chunks, int tokens, string? detail) =>
        new(doc.Id, doc.Norma.Titulo, categoria, rule, status, pages, empty, chunks, tokens, detail);

    private async Task WriteAsync(IReadOnlyList<DocumentReport> reports, IReadOnlyList<DocumentChunk> chunks, CancellationToken ct)
    {
        Directory.CreateDirectory(paths.Root);
        await WriteJsonlAsync(ChunksFile, chunks, ct);
        await WriteJsonlAsync(ReportFile, reports, ct);
    }

    /// <summary>Write to a temp file and move it over the target: a crash never leaves a half-written chunks.jsonl for Phase 3.</summary>
    private static async Task WriteJsonlAsync<T>(string path, IEnumerable<T> items, CancellationToken ct)
    {
        var tmp = path + ".tmp";
        await using (var writer = new StreamWriter(tmp, append: false, new UTF8Encoding(false)))
        {
            foreach (var item in items)
                await writer.WriteLineAsync(JsonSerializer.Serialize(item, ManifestStore.Json).AsMemory(), ct);
        }

        File.Move(tmp, path, overwrite: true);
    }
}
