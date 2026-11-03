using System.Text.RegularExpressions;
using SunatRAG.Core.Documents;
using SunatRAG.Core.Evaluation;

namespace SunatRAG.Ingestion.Scraping;

/// <summary>What the sumilla rules did to one year of index listings, plus the entries a human should eyeball.</summary>
/// <param name="PossiblyMissed">Classified Normativa although the sumilla has administrative wording: candidates for a NEW rule.</param>
/// <param name="PossiblyWrong">Classified Administrativa although the sumilla has tax wording: candidates for a rule that is too greedy.</param>
public sealed record YearAudit(
    int Year,
    int Resoluciones,
    int Informes,
    int Administrativas,
    IReadOnlyList<(string Rule, int Count)> ByRule,
    IReadOnlyList<NormaListing> PossiblyMissed,
    IReadOnlyList<NormaListing> PossiblyWrong,
    int PossiblyMissedTotal,
    int PossiblyWrongTotal)
{
    public double AdministrativePercent => Resoluciones == 0 ? 0 : 100.0 * Administrativas / Resoluciones;
}

/// <summary>
/// Checks the CategoriaClassifier rules, which were calibrated on 2025 only, against other years WITHOUT downloading any PDF: the
/// classifier reads the sumilla, and the sumilla is in the index page. The audit cannot say what is right; it narrows 400 sumillas a year
/// to the few where two independent signals disagree (the rule says one thing, loose keywords say the other).
/// A false "Administrativa" hides a real norm (the costly error, and the one the classifier's default is built to avoid); a false
/// "Normativa" only adds some useless chunks.
/// </summary>
public static partial class RuleAudit
{
    public static IReadOnlyList<YearAudit> Analyze(IEnumerable<NormaListing> listings, int sampleSize = 25) =>
    [
        .. listings
            .GroupBy(l => l.Year)
            .OrderBy(g => g.Key)
            .Select(g => AnalyzeYear(g.Key, g.ToList(), sampleSize))
    ];

    private static YearAudit AnalyzeYear(int year, List<NormaListing> all, int sampleSize)
    {
        var resoluciones = all.Where(l => l.Tipo == TipoNorma.ResolucionSuperintendencia).ToList();
        var classified = resoluciones.Select(l => (Listing: l, Result: CategoriaClassifier.Classify(l.Tipo, l.Sumilla))).ToList();

        var admin = classified.Where(c => c.Result.Categoria == Categoria.Administrativa).ToList();
        var byRule = admin.GroupBy(c => c.Result.Rule).OrderByDescending(g => g.Count()).Select(g => (g.Key, g.Count())).ToList();

        var missed = classified
            .Where(c => c.Result.Categoria == Categoria.Normativa && AdministrativeHint().IsMatch(EvalText.Fold(c.Listing.Sumilla)))
            .Select(c => c.Listing).ToList();
        var wrong = admin
            .Where(c => TaxHint().IsMatch(EntityNames().Replace(EvalText.Fold(c.Listing.Sumilla), " ")))
            .Select(c => c.Listing).ToList();

        return new YearAudit(
            year, resoluciones.Count, all.Count - resoluciones.Count, admin.Count, byRule,
            [.. missed.Take(sampleSize)], [.. wrong.Take(sampleSize)], missed.Count, wrong.Count);
    }

    // Deliberately loose (high recall, low precision): they only pick WHAT TO READ, they never decide anything.
    [GeneratedRegex(
        @"\b(designa|designan|encarga|encargan|viaje|viajes|comision de servicios|contrato|contratos|adjudicacion|plan operativo|plan estrategico|" +
        @"presupuesto|comite|servidores|personal|renuncia|laudo|nombramiento|cese|licencia|vacaciones|capacitacion|convenio)\b",
        RegexOptions.CultureInvariant)]
    private static partial Regex AdministrativeHint();

    // The SUNAT's own name ("Superintendencia Nacional de Aduanas y de Administración Tributaria") contains "tributaria" and "aduanas" and
    // is in most sumillas: left in, EVERY administrative sumilla looked like tax content (first run on 2022-2024: 58 of 196 flagged, all noise).
    [GeneratedRegex(
        @"superintendencia nacional de aduanas y de administracion tributaria|organizacion mundial de aduanas|" +
        @"oficina de aduanas y proteccion fronteriza|foro de administracion tributaria|administraciones? tributarias?",
        RegexOptions.CultureInvariant)]
    private static partial Regex EntityNames();

    // "sunat" is not a hint: it is in nearly every sumilla.
    [GeneratedRegex(
        @"\b(tributari\w*|impuesto|renta|igv|isc|contribuyente\w*|retencion|retenciones|percepcion|percepciones|detraccion|detracciones|" +
        @"comprobante\w*|declaracion|declaraciones|aduan\w*|arancel\w*|infraccion\w*|sancion\w*|regimen|facturacion|libros? y registros)\b",
        RegexOptions.CultureInvariant)]
    private static partial Regex TaxHint();
}
