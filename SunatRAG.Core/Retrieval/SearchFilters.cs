using System.Linq.Expressions;
using System.Text.RegularExpressions;
using SunatRAG.Core.Documents;
using SunatRAG.Core.Indexing;

namespace SunatRAG.Core.Retrieval;

/// <summary>
/// Metadata filters for a search. Defaults: only Normativa, drafts (proyectos) excluded.
///
/// Two kinds of filter, because the two stores are not equally capable:
///  - Pushdown (Tipo, Categoria, Year, dates): all fields declared IsIndexed in <see cref="ChunkCollection"/>. They go to the vector store as a
///    LINQ expression (VectorSearchOptions.Filter), so sqlite-vec only scans matching rows. The very same expression is compiled and
///    applied to the BM25 hits, because FTS5 knows nothing about these columns. One definition of "the filter", two enforcement points.
///  - Post-filter (IncluirProyectos): depends on the sumilla, which is not an indexed column; applied in memory after the search.
/// </summary>
public sealed record SearchFilters
{
    public TipoNorma? Tipo { get; init; }

    /// <summary>Null = both. Default Normativa: administrativas are excluded from indexing anyway, this keeps them out if someone indexes them.</summary>
    public Categoria? Categoria { get; init; } = global::SunatRAG.Core.Documents.Categoria.Normativa;

    public int? Year { get; init; }
    public DateOnly? PublicadoDesde { get; init; }
    public DateOnly? PublicadoHasta { get; init; }

    /// <summary>Drafts ("Dispone la publicación del Proyecto de Resolución...") are not law; excluded unless asked for.</summary>
    public bool IncluirProyectos { get; init; }

    public Expression<Func<ChunkRecord, bool>>? ToPushdownPredicate()
    {
        var clauses = new List<Expression<Func<ChunkRecord, bool>>>();

        // Each clause captures a local: the connector's translator evaluates captured values, and the compiled form works as plain C#.
        if (Tipo is { } tipo) { var name = tipo.ToString(); clauses.Add(r => r.Tipo == name); }
        if (Categoria is { } categoria) { var name = categoria.ToString(); clauses.Add(r => r.Categoria == name); }
        if (Year is { } year) clauses.Add(r => r.Year == year);
        if (PublicadoDesde is { } desde) { var ymd = ChunkRecordMapper.ToYmd(desde); clauses.Add(r => r.FechaPublicacionYmd >= ymd); }
        if (PublicadoHasta is { } hasta) { var ymd = ChunkRecordMapper.ToYmd(hasta); clauses.Add(r => r.FechaPublicacionYmd <= ymd); }

        return clauses.Count == 0 ? null : clauses.Aggregate(AndAlso);
    }

    public Func<ChunkRecord, bool>? ToPostFilter() => IncluirProyectos ? null : r => !ProyectoDetector.IsProyecto(r);

    private static Expression<Func<ChunkRecord, bool>> AndAlso(
        Expression<Func<ChunkRecord, bool>> left, Expression<Func<ChunkRecord, bool>> right)
    {
        var parameter = left.Parameters[0];
        var rightBody = new ReplaceParameter(right.Parameters[0], parameter).Visit(right.Body);
        return Expression.Lambda<Func<ChunkRecord, bool>>(Expression.AndAlso(left.Body, rightBody), parameter);
    }

    private sealed class ReplaceParameter(ParameterExpression from, ParameterExpression to) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) => node == from ? to : base.VisitParameter(node);
    }
}

/// <summary>
/// Recognises "projects of resolution" published for comment. Calibrated on the 2025 corpus: 23 of 81 resoluciones start with
/// "Dispone(n) (la) publicación ... del Proyecto de Resolución". Informes are never drafts, even when their sumilla says "proyecto".
/// </summary>
public static partial class ProyectoDetector
{
    // Runs on the folded sumilla (lowercase, no diacritics). "De dispone" and "Se dispone" occur in the real data.
    [GeneratedRegex(@"^((se|de)\s+)*dispon(e|en)\s+(la\s+)?publicacion\b.*\bproyecto\s+de\s+resolucion", RegexOptions.CultureInvariant)]
    private static partial Regex DraftRule();

    public static bool IsProyecto(string? sumilla) =>
        !string.IsNullOrWhiteSpace(sumilla) && DraftRule().IsMatch(CategoriaClassifier.Fold(sumilla));

    public static bool IsProyecto(ChunkRecord record) =>
        record.Tipo == nameof(TipoNorma.ResolucionSuperintendencia) && IsProyecto(record.Sumilla);
}
