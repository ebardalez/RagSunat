using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SunatRAG.Core.Documents;
using SunatRAG.Ingestion.Scraping;

namespace SunatRAG.Ingestion.Extraction;

/// <param name="IncludeAdministrativas">Default false: administrative resolutions are not indexed.</param>
/// <param name="OnlyId">Debug: process a single document and write nothing to disk.</param>
/// <param name="Incremental">Reuse chunks of documents whose fingerprint (PDF sha256 + metadata + chunking recipe) did not change.</param>
/// <param name="Fingerprint">Identifies the chunking recipe (tokenizer, target, overlap, admin flag); any change invalidates every document.</param>
public sealed record ChunkingRequest(bool IncludeAdministrativas, int? Limit, string? OnlyId, bool Incremental = false, string Fingerprint = "");

/// <summary>What an incremental run did. Removed = in the previous state but no longer in the manifest.</summary>
public sealed record IncrementalStats(int Reused, int Reprocessed, int New, int Removed);

/// <summary>One line of data/extraction-state.jsonl: the fingerprint under which a document was last processed.</summary>
public sealed record DocumentState(string Id, string Key);

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
    bool WroteFiles,
    IncrementalStats? Incremental = null);

/// <summary>
/// manifest.jsonl -> (PdfPig -> normalize -> split -> chunk) -> chunks.jsonl + extraction-report.jsonl.
/// Full runs rewrite everything (seconds, deterministic). With <c>Incremental</c> (Phase 6) only new or changed documents are
/// re-extracted: a document is unchanged when its <see cref="Fingerprint"/> equals the one stored in extraction-state.jsonl.
/// Combined with Phase 3's EmbeddingKey, an unchanged document costs no PDF parsing and no embedding.
/// </summary>
public sealed class ChunkingRunner(DataPaths paths, ManifestStore manifestStore, PdfTextExtractor extractor, ILogger<ChunkingRunner> logger)
{
    public string ChunksFile => Path.Combine(paths.Root, "chunks.jsonl");
    public string ReportFile => Path.Combine(paths.Root, "extraction-report.jsonl");
    public string StateFile => Path.Combine(paths.Root, "extraction-state.jsonl");

    /// <summary>Bump when the extraction/normalization/splitting code changes in a way that alters output.</summary>
    public const string RecipeVersion = "1";

    /// <summary>
    /// Per-document key: PDF hash + every manifest field copied into the chunks + the run recipe + the classifier's VERDICT.
    /// The verdict is in the key so that editing a rule re-processes exactly the documents whose verdict changes (one that becomes
    /// Administrativa must leave the index, one that stops being it must enter) without a manual <see cref="RecipeVersion"/> bump.
    /// Without it, `extract --incremental` would reuse stale chunks after a rule change and nothing would say so.
    /// </summary>
    public static string Fingerprint(NormaDocument doc, string runFingerprint, Func<TipoNorma, string?, CategoriaResult>? classify = null)
    {
        var n = doc.Norma;
        var verdict = (classify ?? CategoriaClassifier.Classify)(n.Tipo, n.Sumilla);
        var raw = string.Join('|', RecipeVersion, runFingerprint, doc.Sha256, doc.Url, n.Titulo, n.Tipo, n.Numero, n.Sumilla,
            n.FechaPublicacion, n.FechaVigencia, verdict.Categoria, verdict.Rule);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(raw)))[..16];
    }

    /// <summary>Pure decision, easy to test: which manifest documents can be reused given the previous state.</summary>
    public static (HashSet<string> Reusable, int New, int Changed, int Removed) Plan(
        IEnumerable<(string Id, string Key)> current, IReadOnlyDictionary<string, string> previous)
    {
        var reusable = new HashSet<string>(StringComparer.Ordinal);
        int isNew = 0, changed = 0;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (id, key) in current)
        {
            seen.Add(id);
            if (!previous.TryGetValue(id, out var old)) isNew++;
            else if (old != key) changed++;
            else reusable.Add(id);
        }

        return (reusable, isNew, changed, previous.Keys.Count(id => !seen.Contains(id)));
    }

    public async Task<ChunkingSummary> RunAsync(TokenAwareChunker chunker, ChunkingRequest request, CancellationToken ct)
    {
        var manifest = await manifestStore.LoadAsync(ct);
        IEnumerable<NormaDocument> docs = manifest.Values.OrderBy(d => d.Id, StringComparer.Ordinal);
        if (request.OnlyId is not null) docs = docs.Where(d => d.Id == request.OnlyId);

        var reports = new List<DocumentReport>();
        var allChunks = new List<DocumentChunk>();
        var states = new List<DocumentState>();
        var processed = 0;

        // Incremental: only for full runs, and only when all three previous files exist (otherwise fall back to a full run).
        var incremental = request.Incremental && request.OnlyId is null && request.Limit is null
                          && File.Exists(ChunksFile) && File.Exists(ReportFile) && File.Exists(StateFile);
        var keys = manifest.Values.ToDictionary(d => d.Id, d => Fingerprint(d, request.Fingerprint), StringComparer.Ordinal);
        Dictionary<string, DocumentReport> oldReports = new();
        ILookup<string, DocumentChunk> oldChunks = Array.Empty<DocumentChunk>().ToLookup(c => c.DocumentId);
        HashSet<string> reusable = [];
        IncrementalStats? stats = null;
        if (incremental)
        {
            var previous = (await ReadJsonlAsync<DocumentState>(StateFile, ct)).ToDictionary(s => s.Id, s => s.Key, StringComparer.Ordinal);
            oldReports = (await ReadJsonlAsync<DocumentReport>(ReportFile, ct)).ToDictionary(r => r.Id, StringComparer.Ordinal);
            oldChunks = (await ReadJsonlAsync<DocumentChunk>(ChunksFile, ct)).ToLookup(c => c.DocumentId, StringComparer.Ordinal);
            var plan = Plan(keys.Select(k => (k.Key, k.Value)), previous);
            reusable = plan.Reusable;
            reusable.RemoveWhere(id => !oldReports.ContainsKey(id)); // state without report: be safe, redo
            stats = new IncrementalStats(reusable.Count, plan.Changed, plan.New, plan.Removed);
        }

        foreach (var doc in docs)
        {
            ct.ThrowIfCancellationRequested();
            if (request.Limit is { } limit && processed >= limit) break;
            processed++;

            if (reusable.Contains(doc.Id))
            {
                reports.Add(oldReports[doc.Id]);
                allChunks.AddRange(oldChunks[doc.Id].OrderBy(c => c.Index));
                states.Add(new DocumentState(doc.Id, keys[doc.Id]));
                continue;
            }

            var (categoria, rule) = CategoriaClassifier.Classify(doc.Norma.Tipo, doc.Norma.Sumilla);
            if (categoria == Categoria.Administrativa && !request.IncludeAdministrativas)
            {
                reports.Add(Report(doc, categoria, rule, "SkippedAdministrativa", 0, 0, 0, 0, null));
                states.Add(new DocumentState(doc.Id, keys[doc.Id]));
                continue;
            }

            var extraction = extractor.Extract(Path.Combine(paths.Root, doc.RelativePath), ct);
            if (extraction.Status != ExtractionStatus.Ok)
            {
                logger.LogWarning("{Id}: {Status} {Error}", doc.Id, extraction.Status, extraction.Error);
                reports.Add(Report(doc, categoria, rule, extraction.Status.ToString(),
                    extraction.PageCount, extraction.EmptyPages, 0, 0, extraction.Error));
                // Failed documents get no state: the next incremental run retries them. (NoText is deterministic; keep its state.)
                if (extraction.Status != ExtractionStatus.Failed) states.Add(new DocumentState(doc.Id, keys[doc.Id]));
                continue;
            }

            var paragraphs = TextNormalizer.ToParagraphs(extraction.Pages);
            var sections = LegalSectionSplitter.Split(paragraphs);
            var chunks = chunker.Chunk(sections);

            var docChunks = chunks.Select((c, i) => ToDocumentChunk(doc, categoria, c, i)).ToList();
            allChunks.AddRange(docChunks);
            reports.Add(Report(doc, categoria, rule, "Ok", extraction.PageCount, extraction.EmptyPages,
                docChunks.Count, docChunks.Sum(c => c.TokenCount), null));
            states.Add(new DocumentState(doc.Id, keys[doc.Id]));
        }

        var write = request.OnlyId is null && request.Limit is null;
        if (write) await WriteAsync(reports, allChunks, states, ct);
        return new ChunkingSummary(reports, allChunks, write, stats);
    }

    private static async Task<List<T>> ReadJsonlAsync<T>(string path, CancellationToken ct)
    {
        var list = new List<T>();
        await foreach (var line in File.ReadLinesAsync(path, ct))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            list.Add(JsonSerializer.Deserialize<T>(line, ManifestStore.Json)!);
        }

        return list;
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

    private async Task WriteAsync(IReadOnlyList<DocumentReport> reports, IReadOnlyList<DocumentChunk> chunks,
        IReadOnlyList<DocumentState> states, CancellationToken ct)
    {
        Directory.CreateDirectory(paths.Root);
        // State last: if we crash in between, the stale state only causes extra work, never wrong reuse.
        await WriteJsonlAsync(ChunksFile, chunks, ct);
        await WriteJsonlAsync(ReportFile, reports, ct);
        await WriteJsonlAsync(StateFile, states, ct);
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
