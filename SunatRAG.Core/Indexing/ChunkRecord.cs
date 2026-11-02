using System.Text.RegularExpressions;
using Microsoft.Extensions.VectorData;
using SunatRAG.Core.Documents;

namespace SunatRAG.Core.Indexing;

/// <summary>
/// The vector-store record: one retrievable chunk. A plain mutable class because the VectorData mapper needs a
/// parameterless constructor and settable properties (our immutable DocumentChunk is the pipeline's currency;
/// this one is the storage shape).
///
/// The schema is declared in <see cref="ChunkCollection.CreateDefinition"/> instead of with
/// [VectorStoreKey]/[VectorStoreData]/[VectorStoreVector] attributes, because attribute arguments must be
/// compile-time constants and the embedding dimension comes from configuration (Ai:Ollama:EmbeddingDimensions).
/// </summary>
public sealed class ChunkRecord
{
    public string ChunkId { get; set; } = "";
    public string DocumentId { get; set; } = "";
    public int ChunkIndex { get; set; }

    public string Text { get; set; } = "";
    public string? Heading { get; set; }
    public string Titulo { get; set; } = "";
    public string Numero { get; set; } = "";
    public string? Sumilla { get; set; }

    /// <summary>TipoNorma enum name ("ResolucionSuperintendencia", "Informe"...). Stored as text so the filter reads naturally.</summary>
    public string Tipo { get; set; } = "";

    /// <summary>Categoria enum name ("Normativa" | "Administrativa").</summary>
    public string Categoria { get; set; } = "";

    /// <summary>Year of the norma, derived from the document id. 0 = unknown.</summary>
    public int Year { get; set; }

    /// <summary>yyyymmdd as int (null = the index page had no date). SQLite has no date type; a sortable int filters with plain comparisons.</summary>
    public int? FechaPublicacionYmd { get; set; }

    public int? FechaVigenciaYmd { get; set; }

    public int PageStart { get; set; }
    public int PageEnd { get; set; }
    public int TokenCount { get; set; }
    public string Url { get; set; } = "";

    /// <summary>DocumentChunk.TextHash (of Text only).</summary>
    public string TextHash { get; set; } = "";

    /// <summary>"{TextHash}|{model}|{recipe}": what the stored vector was computed from. Resumability compares this.</summary>
    public string EmbeddingKey { get; set; } = "";

    public ReadOnlyMemory<float> Embedding { get; set; }

    /// <summary>"Resolución de Superintendencia N° 000401-2025, pág. 2 (Artículo 3.- Objeto)".</summary>
    public string Cita()
    {
        var pages = PageEnd > PageStart ? $"págs. {PageStart}-{PageEnd}" : $"pág. {PageStart}";
        return string.IsNullOrWhiteSpace(Heading) ? $"{Titulo}, {pages}" : $"{Titulo}, {pages} ({Heading})";
    }
}

public static partial class ChunkRecordMapper
{
    /// <summary>
    /// The text that actually gets embedded. A bare chunk ("Artículo 3.- Plazo: cinco días hábiles") is ambiguous out of context;
    /// prefixing the norma title and the heading path lets the vector carry "which resolución, which artículo".
    /// The stored Text stays untouched (it is what the user sees and what the LLM quotes).
    /// </summary>
    public static string EmbeddingText(DocumentChunk c) =>
        string.IsNullOrWhiteSpace(c.Heading) ? $"{c.Titulo}\n{c.Text}" : $"{c.Titulo} — {c.Heading}\n{c.Text}";

    public static string EmbeddingKey(DocumentChunk c, string model, string recipe) => $"{c.TextHash}|{model}|{recipe}";

    public static int ToYmd(DateOnly d) => d.Year * 10_000 + d.Month * 100 + d.Day;

    /// <summary>"superin-2025-000401-2025" -> 2025; "oficios-2025-..." -> 2025.</summary>
    public static int YearOf(string documentId)
    {
        var m = IdYear().Match(documentId);
        return m.Success ? int.Parse(m.Groups[1].Value) : 0;
    }

    public static ChunkRecord ToRecord(DocumentChunk c, string embeddingKey, ReadOnlyMemory<float> vector) => new()
    {
        ChunkId = c.ChunkId,
        DocumentId = c.DocumentId,
        ChunkIndex = c.Index,
        Text = c.Text,
        Heading = c.Heading,
        Titulo = c.Titulo,
        Numero = c.Numero,
        Sumilla = c.Sumilla,
        Tipo = c.Tipo.ToString(),
        Categoria = c.Categoria.ToString(),
        Year = YearOf(c.DocumentId),
        FechaPublicacionYmd = c.FechaPublicacion is { } p ? ToYmd(p) : null,
        FechaVigenciaYmd = c.FechaVigencia is { } v ? ToYmd(v) : null,
        PageStart = c.PageStart,
        PageEnd = c.PageEnd,
        TokenCount = c.TokenCount,
        Url = c.Url.ToString(),
        TextHash = c.TextHash,
        EmbeddingKey = embeddingKey,
        Embedding = vector
    };

    [GeneratedRegex(@"^[a-z]+-(\d{4})(?:-|$)")]
    private static partial Regex IdYear();
}

/// <summary>The VectorData schema for <see cref="ChunkRecord"/>.</summary>
public static class ChunkCollection
{
    public static VectorStoreCollectionDefinition CreateDefinition(int dimensions) => new()
    {
        Properties =
        [
            new VectorStoreKeyProperty(nameof(ChunkRecord.ChunkId), typeof(string)),

            // IsIndexed = filterable (the Phase 4 filters: tipo / categoria / year / fecha).
            new VectorStoreDataProperty(nameof(ChunkRecord.DocumentId), typeof(string)) { IsIndexed = true },
            new VectorStoreDataProperty(nameof(ChunkRecord.Tipo), typeof(string)) { IsIndexed = true },
            new VectorStoreDataProperty(nameof(ChunkRecord.Categoria), typeof(string)) { IsIndexed = true },
            new VectorStoreDataProperty(nameof(ChunkRecord.Year), typeof(int)) { IsIndexed = true },
            new VectorStoreDataProperty(nameof(ChunkRecord.FechaPublicacionYmd), typeof(int?)) { IsIndexed = true },
            new VectorStoreDataProperty(nameof(ChunkRecord.FechaVigenciaYmd), typeof(int?)),

            new VectorStoreDataProperty(nameof(ChunkRecord.ChunkIndex), typeof(int)),
            new VectorStoreDataProperty(nameof(ChunkRecord.Text), typeof(string)),
            new VectorStoreDataProperty(nameof(ChunkRecord.Heading), typeof(string)),
            new VectorStoreDataProperty(nameof(ChunkRecord.Titulo), typeof(string)),
            new VectorStoreDataProperty(nameof(ChunkRecord.Numero), typeof(string)),
            new VectorStoreDataProperty(nameof(ChunkRecord.Sumilla), typeof(string)),
            new VectorStoreDataProperty(nameof(ChunkRecord.PageStart), typeof(int)),
            new VectorStoreDataProperty(nameof(ChunkRecord.PageEnd), typeof(int)),
            new VectorStoreDataProperty(nameof(ChunkRecord.TokenCount), typeof(int)),
            new VectorStoreDataProperty(nameof(ChunkRecord.Url), typeof(string)),
            new VectorStoreDataProperty(nameof(ChunkRecord.TextHash), typeof(string)),
            new VectorStoreDataProperty(nameof(ChunkRecord.EmbeddingKey), typeof(string)),

            // sqlite-vec "vec0" table, brute-force (Flat) scan. At ~1k chunks that is sub-millisecond; HNSW is not offered by sqlite-vec.
            new VectorStoreVectorProperty(nameof(ChunkRecord.Embedding), typeof(ReadOnlyMemory<float>), dimensions)
            {
                DistanceFunction = DistanceFunction.CosineDistance,
                IndexKind = IndexKind.Flat
            }
        ]
    };
}
