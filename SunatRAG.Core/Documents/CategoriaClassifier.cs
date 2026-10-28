using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace SunatRAG.Core.Documents;

/// <summary>Result plus the name of the rule that fired, so every decision is auditable in the extraction report.</summary>
public sealed record CategoriaResult(Categoria Categoria, string Rule);

/// <summary>
/// Rule-based filter over the sumilla. Rules were calibrated on the real 2025 corpus (401 resoluciones):
/// ~270 of them are personnel / travel / contract / budget housekeeping.
/// Everything that is not matched is Normativa: a false "Normativa" costs a few useless chunks,
/// a false "Administrativa" hides a real norm, so the default errs on the side of keeping.
/// </summary>
public static class CategoriaClassifier
{
    private sealed record Rule(string Name, Regex Pattern);

    // Patterns run on the FOLDED sumilla: lowercase, no diacritics, single spaces.
    private static readonly Rule[] AdministrativeRules =
    [
        // "Designa y excluye agentes de retención" is a real tax norm: the lookahead keeps it out.
        new("personal:designacion", Rx(@"^designa(n)?\b(?!\s+(y\s+excluye|agentes?|sujetos|emisores?|operadores?))")),
        new("personal:encargatura", Rx(@"^encarga(n)?\b")),
        new("personal:deja-sin-efecto", Rx(@"^deja(n)?\s+sin\s+efecto\s+(y\s+designa|(la\s+|las\s+|el\s+|los\s+)?(designacion|designaciones|encargatura|encargaturas|encargo|encargos|encarga|autorizacion\s+de\s+viaje))")),
        new("personal:conclusion-renuncia", Rx(@"^(da(n)?\s+por\s+(concluid|terminad)|(se\s+)?acepta(n)?\s+(la\s+)?renuncia)")),
        new("viaje", Rx(@"^(autoriza(n)?\s+(el\s+|la\s+)?(viaje|comision\s+de\s+servicios|participacion)|viaje\s+de\s+servidores)")),
        new("contratacion", Rx(@"^(aprueba(n)?\s+(la\s+)?modificacion|declara(n)?\s+(la\s+)?nulidad)\b.*\b(contrato|adjudicacion|concurso\s+publico|orden\s+de\s+compra)\b")),
        new("contratacion:laudo", Rx(@"\blaudo\s+arbitral\b")),
        new("presupuesto", Rx(@"^(aprueba(n)?\s+(la\s+)?(desagregacion|reduccion\s+del\s+presupuesto)|autoriza(n)?\s+(la\s+)?transferencia\s+financiera|autoriza(n)?\s+la\s+fase\s+de\s+ejecucion)")),
        new("planeamiento", Rx(@"^(aprueba(n)?|actualiza|modifica(n)?)\s+(modificar\s+)?(la\s+|el\s+|las\s+|los\s+)?(modificacion\s+(del|de\s+la)\s+)?(plan\s+(operativo|estrategico|de\s+continuidad|de\s+estrategia|de\s+implementacion|anual)|presupuesto\s+(institucional|analitico)|programa\s+de\s+integridad|lista\s+priorizada\s+de\s+obras|cuadro\s+para|manual\s+de\s+organizacion)")),
        new("planeamiento:set-indicadores", Rx(@"\bset\s+de\s+indicadores\s+y\s+proyectos\b")),
        // Lookahead: "Conforma Comité Revisor a que se refiere el artículo 62-C del Código Tributario" is tax content.
        new("organizacion:comision-equipo", Rx(@"^(?!.*\bcodigo\s+tributario\b)(conforma(n)?|crea(n)?|constituye(n)?)\s+(la\s+|el\s+)?(comision|equipo|grupo\s+de\s+trabajo|comite)")),
        new("organizacion:comite-conformacion", Rx(@"^(actualiza\s+la\s+conformacion|modifica\s+(la\s+)?constitucion)\s+del\s+comite")),
        new("tesoreria:servidores-autorizados", Rx(@"^modifica(n)?\s+la\s+relacion\s+de\s+servidores"))
    ];

    public static CategoriaResult Classify(TipoNorma tipo, string? sumilla)
    {
        // Informes (doctrina oficial) and any non-resolución type are always content we want.
        if (tipo != TipoNorma.ResolucionSuperintendencia)
            return new(Categoria.Normativa, "tipo:no-resolucion");

        if (string.IsNullOrWhiteSpace(sumilla))
            return new(Categoria.Normativa, "sin-sumilla");

        var folded = Fold(sumilla);
        foreach (var rule in AdministrativeRules)
            if (rule.Pattern.IsMatch(folded))
                return new(Categoria.Administrativa, rule.Name);

        return new(Categoria.Normativa, "default");
    }

    /// <summary>Lowercase, strip diacritics, collapse whitespace. "Aprobación  del" -> "aprobacion del".</summary>
    internal static string Fold(string text)
    {
        var decomposed = text.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var ch in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            sb.Append(char.IsWhiteSpace(ch) ? ' ' : char.ToLowerInvariant(ch));
        }

        return Regex.Replace(sb.ToString(), @"\s{2,}", " ").Trim();
    }

    private static Regex Rx(string pattern) =>
        new(pattern, RegexOptions.Compiled | RegexOptions.CultureInvariant);
}
