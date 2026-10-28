namespace SunatRAG.Core.Documents;

/// <summary>
/// Coarse content class computed from the sumilla (see <see cref="CategoriaClassifier"/>).
/// Administrativa = designaciones, viajes, contratos, presupuesto interno: legally public, useless for tax Q&amp;A.
/// </summary>
public enum Categoria
{
    Normativa,
    Administrativa
}

/// <summary>
/// One retrievable passage, with everything Phase 3 needs to build the vector record and everything
/// Phase 5 needs to render a citation. Flat on purpose: it maps 1:1 onto a vector-store record.
/// </summary>
/// <param name="ChunkId">"{documentId}#{index:0000}". Deterministic: same input + same chunking options = same id.</param>
/// <param name="TextHash">SHA-256 (hex, first 16 chars) of <paramref name="Text"/>. Phase 3 skips re-embedding when id and hash are unchanged.</param>
/// <param name="PageStart">1-based page where the chunk starts (what a citation prints).</param>
/// <param name="Heading">Structural path such as "Artículo 3.- Objeto" or "DISPOSICIONES COMPLEMENTARIAS FINALES &gt; Primera". Null = preámbulo.</param>
public sealed record DocumentChunk(
    string ChunkId,
    string DocumentId,
    int Index,
    string Text,
    int TokenCount,
    string TextHash,
    int PageStart,
    int PageEnd,
    string? Heading,
    string Titulo,
    TipoNorma Tipo,
    string Numero,
    string? Sumilla,
    DateOnly? FechaPublicacion,
    DateOnly? FechaVigencia,
    Uri Url,
    Categoria Categoria)
{
    /// <summary>"Resolución de Superintendencia N° 000401-2025, pág. 2 (Artículo 3.- Objeto)".</summary>
    public string Cita()
    {
        var pages = PageEnd > PageStart ? $"págs. {PageStart}-{PageEnd}" : $"pág. {PageStart}";
        return string.IsNullOrWhiteSpace(Heading) ? $"{Titulo}, {pages}" : $"{Titulo}, {pages} ({Heading})";
    }
}
