using System.Globalization;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using SunatRAG.Core.Documents;

namespace SunatRAG.Ingestion.Scraping;

/// <summary>A per-year index page found from a seed page.</summary>
public sealed record IndexPage(TipoNorma Tipo, int Year, Uri Url);

public sealed record ParsedIndex(IReadOnlyList<NormaListing> Normas, int SkippedNonPdf);

/// <summary>
/// Pure functions HTML -> records. No I/O, so every quirk of the site gets a unit test with a fixture.
/// Layouts differ by era (2016 uses &lt;font&gt; wrappers, 1999 links to .htm instead of PDF), so we do not
/// depend on CSS classes: a "norma row" is any leaf &lt;tr&gt; whose first cell links to something labelled "N° ...".
/// </summary>
public static partial class SunatIndexParser
{
    [GeneratedRegex(@"^/legislacion/(?<kind>superin|oficios)/(?<year>\d{4})/(?:indices/)?indcor\.html?$", RegexOptions.IgnoreCase)]
    private static partial Regex IndexPathRegex();

    [GeneratedRegex(@"N\s*\.?\s*[°º]\s*(?<n>\S+)")]
    private static partial Regex NumeroRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    /// <summary>Finds the per-year index links (superin/YYYY/indices/indcor.htm, oficios/YYYY/indcor.htm).</summary>
    public static IReadOnlyList<IndexPage> DiscoverIndexPages(string html, Uri pageUrl)
    {
        var doc = new HtmlParser().ParseDocument(html);
        var pages = new Dictionary<Uri, IndexPage>();

        foreach (var a in doc.QuerySelectorAll("a[href]"))
        {
            if (!Uri.TryCreate(pageUrl, a.GetAttribute("href"), out var url)) continue;
            var match = IndexPathRegex().Match(url.AbsolutePath);
            if (!match.Success) continue;

            var tipo = match.Groups["kind"].Value.Equals("superin", StringComparison.OrdinalIgnoreCase)
                ? TipoNorma.ResolucionSuperintendencia
                : TipoNorma.Informe;
            var year = int.Parse(match.Groups["year"].Value, CultureInfo.InvariantCulture);
            pages[url] = new IndexPage(tipo, year, url);
        }

        return [.. pages.Values];
    }

    /// <param name="defaultTipo">Used when the row label does not say (superin pages: resolución; oficios pages: informe).</param>
    public static ParsedIndex ParseNormaIndex(string html, Uri pageUrl, TipoNorma defaultTipo, int year)
    {
        var doc = new HtmlParser().ParseDocument(html);
        var normas = new List<NormaListing>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var skippedNonPdf = 0;

        foreach (var row in doc.QuerySelectorAll("tr"))
        {
            if (row.QuerySelector("table") is not null) continue; // layout table wrapping other tables

            var cells = row.Children.Where(c => c.LocalName is "td" or "th").ToList();
            if (cells.Count < 2) continue;

            var anchor = cells[0].QuerySelector("a[href]");
            if (anchor is null) continue;

            var label = Normalize(anchor.TextContent);
            var numeroMatch = NumeroRegex().Match(label);
            if (!numeroMatch.Success) continue;

            if (!Uri.TryCreate(pageUrl, anchor.GetAttribute("href"), out var url)) continue;
            if (!IsPdf(url)) { skippedNonPdf++; continue; }
            if (!seen.Add(url.AbsoluteUri)) continue;

            var tipo = TipoFromLabel(label, defaultTipo);
            var numero = numeroMatch.Groups["n"].Value;

            var summaryCell = cells[1];
            var anexos = new List<AnexoLink>();
            foreach (var a in summaryCell.QuerySelectorAll("a[href]"))
            {
                if (!Uri.TryCreate(pageUrl, a.GetAttribute("href"), out var anexoUrl) || !IsPdf(anexoUrl)) continue;
                var anexoLabel = Normalize(a.TextContent);
                anexos.Add(new AnexoLink(anexoLabel.Length > 0 ? anexoLabel : "Anexo", anexoUrl));
            }

            // The sumilla is the cell text minus the "Anexo 1 - Anexo 2 ..." links.
            var clone = (IElement)summaryCell.Clone(true);
            foreach (var a in clone.QuerySelectorAll("a").ToList()) a.Remove();
            var sumilla = Normalize(clone.TextContent).Trim(' ', '-', '–', '—');

            var fecha = cells.Count >= 3 ? ParseDate(Normalize(cells[^1].TextContent)) : null;

            normas.Add(new NormaListing(
                Id: NormaIds.FromUrl(url),
                Tipo: tipo,
                Year: year,
                Numero: numero,
                Titulo: $"{tipo.DisplayName()} N° {numero}",
                Sumilla: sumilla.Length > 0 ? sumilla : null,
                FechaPublicacion: fecha,
                FechaVigencia: null,
                Url: url,
                Anexos: anexos));
        }

        return new ParsedIndex(normas, skippedNonPdf);
    }

    private static TipoNorma TipoFromLabel(string label, TipoNorma fallback) =>
        label.StartsWith("Informe", StringComparison.OrdinalIgnoreCase) ? TipoNorma.Informe
        : label.StartsWith("Oficio", StringComparison.OrdinalIgnoreCase) ? TipoNorma.Oficio
        : label.StartsWith("Carta", StringComparison.OrdinalIgnoreCase) ? TipoNorma.Carta
        : fallback;

    private static bool IsPdf(Uri url) => url.AbsolutePath.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase);

    private static DateOnly? ParseDate(string text) =>
        DateOnly.TryParseExact(text, "d/M/yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;

    private static string Normalize(string text) =>
        WhitespaceRegex().Replace(text.Replace('\u00A0', ' '), " ").Trim();
}
