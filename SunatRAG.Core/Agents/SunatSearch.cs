using System.ComponentModel;
using System.Globalization;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SunatRAG.Core.Configuration;
using SunatRAG.Core.Documents;
using SunatRAG.Core.Retrieval;

namespace SunatRAG.Core.Agents;

/// <param name="Text">What the model reads: the numbered sources, or an explicit "nothing found" message.</param>
/// <param name="Chunks">The chunks with the number they were given in the conversation.</param>
/// <param name="Weak">Best cosine distance is above the (uncalibrated) threshold, or nothing was found.</param>
public sealed record SearchOutcome(
    string Text,
    IReadOnlyList<(int Number, RetrievedChunk Chunk)> Chunks,
    bool Weak,
    RetrievalDiagnostics? Diagnostics);

/// <summary>
/// The one place where "agent asks for sources" is implemented. Two front-ends share it:
///   - <see cref="AsTool"/>: an AIFunction the model calls (RetrievalStrategy.Tool);
///   - <see cref="RetrievalContextProvider"/>: an AIContextProvider that calls <see cref="SearchAsync"/> itself (RetrievalStrategy.ContextProvider).
/// One instance per conversation, because it owns the conversation's <see cref="CitationRegistry"/>.
///
/// Depends on IChunkRetriever only: no SQLite, no Ollama, no Agent Framework type except AIFunction (which is Microsoft.Extensions.AI).
/// </summary>
public sealed class SunatSearch(
    IChunkRetriever retriever,
    CitationRegistry registry,
    IOptions<AgentOptions> options,
    ILogger<SunatSearch> logger)
{
    public const string ToolName = "buscar_normativa";

    private const string ToolDescription =
        "Busca fragmentos en el índice de normas SUNAT (resoluciones de superintendencia e informes). " +
        "Devuelve fragmentos numerados [n] con su documento y página. Úsala antes de responder cualquier pregunta sobre normativa tributaria.";

    /// <summary>
    /// The tool description and parameter descriptions are part of the prompt: they are the only documentation the model has for the tool.
    /// Note the instruction to REWRITE the query: the Phase 4 measurements showed hybrid search does not fix vocabulary mismatch
    /// ("hasta cuándo declaro lo del año pasado" misses "cronograma de vencimientos de la declaración jurada anual"); the model can.
    /// </summary>
    public AIFunction AsTool() => AIFunctionFactory.Create(BuscarNormativaAsync, ToolName, ToolDescription);

    public async Task<string> BuscarNormativaAsync(
        [Description("Consulta de búsqueda en español, reformulada con vocabulario normativo (p. ej. 'cronograma de vencimiento declaración jurada anual impuesto a la renta'), no copiada literalmente de la pregunta del usuario.")]
        string consulta,
        [Description("Opcional. 'resolucion' para resoluciones de superintendencia o 'informe' para informes de SUNAT. Omítelo si no estás seguro.")]
        string? tipo = null,
        [Description("Opcional. Año de la norma (el índice cubre solo 2025). Omítelo si no estás seguro.")]
        int? anio = null,
        CancellationToken cancellationToken = default)
    {
        var outcome = await SearchAsync(consulta, tipo, anio, cancellationToken);
        return outcome.Text;
    }

    public async Task<SearchOutcome> SearchAsync(string consulta, string? tipo, int? anio, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(consulta))
            return new SearchOutcome("Error: la consulta está vacía. Llama de nuevo con una consulta de búsqueda.", [], true, null);

        var filters = new SearchFilters { Tipo = ParseTipo(tipo), Year = anio is > 1900 and < 2200 ? anio : null };
        var result = await retriever.RetrieveAsync(new RetrievalRequest(consulta, filters), ct);

        var numbered = result.Chunks.Select(c => (registry.Register(c), c)).ToList();
        var weak = numbered.Count == 0
            || (result.Diagnostics.BestVectorDistance is { } best && best > options.Value.WeakMatchDistance);

        logger.LogInformation(
            "{Tool}(\"{Query}\", tipo={Tipo}, anio={Anio}) -> {Count} fragmentos, mejor distancia {Best}, débil={Weak}",
            ToolName, consulta, tipo, anio, numbered.Count,
            result.Diagnostics.BestVectorDistance?.ToString("0.000", CultureInfo.InvariantCulture) ?? "n/d", weak);

        return new SearchOutcome(Render(consulta, numbered, result.Diagnostics, weak), numbered, weak, result.Diagnostics);
    }

    private string Render(
        string consulta, IReadOnlyList<(int Number, RetrievedChunk Chunk)> numbered, RetrievalDiagnostics diagnostics, bool weak)
    {
        if (numbered.Count == 0)
            return "No se encontraron fragmentos en el índice para esta consulta. NO respondas con conocimiento propio: " +
                   "dile al usuario que el índice no contiene información sobre esto. Puedes reintentar UNA vez con otras palabras.";

        var header = weak
            ? $"AVISO: coincidencia débil (distancia {diagnostics.BestVectorDistance?.ToString("0.00", CultureInfo.InvariantCulture)}). " +
              "Los fragmentos pueden no responder la pregunta: cítalos solo si de verdad contienen la respuesta; si no, dile al usuario que no la encontraste.\n\n"
            : "";

        return $"{header}Fragmentos para \"{consulta}\":\n\n{RetrievalContextFormatter.Format(numbered)}";
    }

    public static TipoNorma? ParseTipo(string? tipo) =>
        Fold(tipo) switch
        {
            "resolucion" or "resoluciones" or "resolucion de superintendencia" => TipoNorma.ResolucionSuperintendencia,
            "informe" or "informes" => TipoNorma.Informe,
            _ => null // unknown or empty: no filter (a wrong filter silently hides the right document, a missing one only costs ranking)
        };

    private static string Fold(string? s) => string.IsNullOrWhiteSpace(s) ? "" : CategoriaClassifier.Fold(s.Trim());
}
