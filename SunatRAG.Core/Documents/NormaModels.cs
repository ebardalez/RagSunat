using System.Text;

namespace SunatRAG.Core.Documents;

public enum TipoNorma
{
    ResolucionSuperintendencia,
    Informe,
    Oficio,
    Carta
}

public enum DocumentRole
{
    /// <summary>The PDF the index row points to.</summary>
    Principal,

    /// <summary>A PDF linked from the sumilla cell (anexos, formularios...).</summary>
    Anexo
}

public static class TipoNormaExtensions
{
    public static string DisplayName(this TipoNorma tipo) => tipo switch
    {
        TipoNorma.ResolucionSuperintendencia => "Resolución de Superintendencia",
        TipoNorma.Informe => "Informe",
        TipoNorma.Oficio => "Oficio",
        TipoNorma.Carta => "Carta",
        _ => tipo.ToString()
    };
}

public sealed record AnexoLink(string Label, Uri Url);

/// <summary>
/// One row of a SUNAT index page: what we know BEFORE downloading anything.
/// </summary>
/// <param name="Id">Stable slug derived from the PDF URL (see <see cref="NormaIds"/>).</param>
/// <param name="Numero">As printed by SUNAT, e.g. "000401-2025" or "0000131-2025-SUNAT/7T0000".</param>
/// <param name="Titulo">Display title used in citations, e.g. "Resolución de Superintendencia N° 000401-2025".</param>
/// <param name="Sumilla">One-line summary from the index (anexo links stripped).</param>
/// <param name="FechaPublicacion">
/// Resoluciones: "Publicado en El Peruano". Informes: the date shown in the index row.
/// Null when the index leaves the cell empty.
/// </param>
/// <param name="FechaVigencia">
/// Not present in the indexes. Stays null in Phase 1; Phase 2 can try to extract it from the PDF text.
/// </param>
public sealed record NormaListing(
    string Id,
    TipoNorma Tipo,
    int Year,
    string Numero,
    string Titulo,
    string? Sumilla,
    DateOnly? FechaPublicacion,
    DateOnly? FechaVigencia,
    Uri Url,
    IReadOnlyList<AnexoLink> Anexos);

/// <summary>
/// One line of data/manifest.jsonl: a PDF that is on disk, plus the metadata the chunker will attach to every chunk.
/// </summary>
/// <param name="RelativePath">Relative to the data root, forward slashes, content-addressed: pdfs/ab/ab12...pdf.</param>
public sealed record NormaDocument(
    string Id,
    DocumentRole Role,
    string? ParentId,
    string? AnexoLabel,
    NormaListing Norma,
    Uri Url,
    string Sha256,
    string RelativePath,
    long SizeBytes,
    DateTimeOffset DownloadedAtUtc,
    string? ETag,
    DateTimeOffset? LastModified);

public static class NormaIds
{
    /// <summary>
    /// "https://www.sunat.gob.pe/legislacion/superin/2025/000401-2025.pdf" -> "superin-2025-000401-2025".
    /// Deterministic and unique per URL, so re-runs find the same id and the manifest is idempotent.
    /// </summary>
    public static string FromUrl(Uri url)
    {
        var path = url.AbsolutePath;
        const string marker = "/legislacion/";
        var at = path.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (at >= 0) path = path[(at + marker.Length)..];

        var lastSlash = path.LastIndexOf('/');
        var dot = path.LastIndexOf('.');
        if (dot > lastSlash) path = path[..dot];

        var sb = new StringBuilder(path.Length);
        foreach (var ch in path.ToLowerInvariant())
        {
            var keep = ch is (>= 'a' and <= 'z') or (>= '0' and <= '9');
            if (keep) sb.Append(ch);
            else if (sb.Length > 0 && sb[^1] != '-') sb.Append('-');
        }

        return sb.ToString().Trim('-');
    }
}
