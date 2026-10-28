using SunatRAG.Core.Documents;
using SunatRAG.Ingestion.Scraping;

namespace SunatRAG.Tests.Scraping;

public class SunatIndexParserTests
{
    private static readonly Uri SuperinPage = new("https://www.sunat.gob.pe/legislacion/superin/2025/indices/indcor.htm");
    private static readonly Uri OficiosPage = new("https://www.sunat.gob.pe/legislacion/oficios/2025/indcor.htm");

    // Trimmed from the real 2025 page: layout wrapper table, header row, a row with anexos and no date, a dated row.
    private const string Superin2025 = """
        <html><body>
        <table><tr><td>
          <h2>INDICE CORRELATIVO DE RESOLUCIONES DE SUPERINTENDENCIA DEL 2025</h2>
          <table>
            <tr>
              <td class="tbd">Resolución</td><td class="tbd">Sumilla</td>
              <td class="tbd">Publicado en el Diario Oficial<br>"El Peruano"</td>
            </tr>
            <tr>
              <td><a href="../000401-2025.pdf" target="_blank">R. Superintendencia N° 000401-2025</a></td>
              <td>Aprueba el set de indicadores y proyectos - <a href="../anexo1-000401-2025.pdf">Anexo 1</a> - <a href="../anexo2-000401-2025.pdf">Anexo 2</a></td>
              <td>&nbsp;</td>
            </tr>
            <tr>
              <td><a href="../000399-2025.pdf">R. Superintendencia N&ordm; 000399-2025</a></td>
              <td>Modifican   el Reglamento
                  de Comprobantes de Pago</td>
              <td>31/12/2025</td>
            </tr>
          </table>
        </td></tr></table>
        </body></html>
        """;

    private const string Oficios2025 = """
        <html><body>
        <p><a name="informes"><strong>INFORMES</strong></a></p>
        <table>
          <tr>
            <td><a href="informe-oficios/i000131-2025-7T0000.pdf"> Informe N° 0000131-2025-SUNAT/7T0000</a></td>
            <td><p>La falta de presentación oportuna del PDB no impide la compensación.</p></td>
            <td>08/01/2026</td>
          </tr>
        </table>
        <p><a name="oficios"><strong>OFICIOS</strong></a></p>
        <table><tr><td>&nbsp;</td><td><p>&nbsp;</p></td><td>&nbsp;</td></tr></table>
        </body></html>
        """;

    private const string Superin1999 = """
        <table>
          <tr>
            <td><strong><a href="/legislacion/superin/1999/003.htm">R. Superintendencia N&ordm; 003</a></strong></td>
            <td>Dictan Disposiciones sobre Comunicaci&oacute;n de Suspensi&oacute;n de Retenciones</td>
          </tr>
        </table>
        """;

    [Fact]
    public void Resoluciones_are_parsed_with_number_title_sumilla_date_and_absolute_urls()
    {
        var parsed = SunatIndexParser.ParseNormaIndex(Superin2025, SuperinPage, TipoNorma.ResolucionSuperintendencia, 2025);

        Assert.Equal(2, parsed.Normas.Count);
        Assert.Equal(0, parsed.SkippedNonPdf);

        var first = parsed.Normas[0];
        Assert.Equal("superin-2025-000401-2025", first.Id);
        Assert.Equal(TipoNorma.ResolucionSuperintendencia, first.Tipo);
        Assert.Equal("000401-2025", first.Numero);
        Assert.Equal("Resolución de Superintendencia N° 000401-2025", first.Titulo);
        Assert.Equal("Aprueba el set de indicadores y proyectos", first.Sumilla); // anexo links stripped
        Assert.Null(first.FechaPublicacion);
        Assert.Null(first.FechaVigencia);
        Assert.Equal("https://www.sunat.gob.pe/legislacion/superin/2025/000401-2025.pdf", first.Url.AbsoluteUri);
        Assert.Equal(["Anexo 1", "Anexo 2"], first.Anexos.Select(a => a.Label));
        Assert.Equal("https://www.sunat.gob.pe/legislacion/superin/2025/anexo2-000401-2025.pdf", first.Anexos[1].Url.AbsoluteUri);

        var second = parsed.Normas[1];
        Assert.Equal("000399-2025", second.Numero); // "Nº" (ordinal indicator) works too
        Assert.Equal("Modifican el Reglamento de Comprobantes de Pago", second.Sumilla); // whitespace collapsed
        Assert.Equal(new DateOnly(2025, 12, 31), second.FechaPublicacion);
        Assert.Empty(second.Anexos);
    }

    [Fact]
    public void Informes_use_the_label_to_set_the_type_and_keep_the_slash_in_the_number()
    {
        var parsed = SunatIndexParser.ParseNormaIndex(Oficios2025, OficiosPage, TipoNorma.Informe, 2025);

        var informe = Assert.Single(parsed.Normas); // the empty OFICIOS table yields nothing
        Assert.Equal(TipoNorma.Informe, informe.Tipo);
        Assert.Equal("0000131-2025-SUNAT/7T0000", informe.Numero);
        Assert.Equal("oficios-2025-informe-oficios-i000131-2025-7t0000", informe.Id);
        Assert.Equal(new DateOnly(2026, 1, 8), informe.FechaPublicacion);
        Assert.Equal(2025, informe.Year); // the index year, not the date year
        Assert.Equal(
            "https://www.sunat.gob.pe/legislacion/oficios/2025/informe-oficios/i000131-2025-7T0000.pdf",
            informe.Url.AbsoluteUri);
    }

    [Fact]
    public void Old_html_normas_are_counted_as_skipped_not_returned()
    {
        var parsed = SunatIndexParser.ParseNormaIndex(
            Superin1999, new Uri("https://www.sunat.gob.pe/legislacion/superin/1999/indices/indcor.htm"),
            TipoNorma.ResolucionSuperintendencia, 1999);

        Assert.Empty(parsed.Normas);
        Assert.Equal(1, parsed.SkippedNonPdf);
    }

    [Fact]
    public void The_same_pdf_listed_twice_is_returned_once()
    {
        var html = """
            <table>
              <tr><td><a href="../1-2020.pdf">R. Superintendencia N° 1-2020</a></td><td>A</td></tr>
              <tr><td><a href="../1-2020.pdf">R. Superintendencia N° 1-2020</a></td><td>A (repetida)</td></tr>
            </table>
            """;

        var parsed = SunatIndexParser.ParseNormaIndex(html, SuperinPage, TipoNorma.ResolucionSuperintendencia, 2020);

        Assert.Single(parsed.Normas);
    }

    [Fact]
    public void Discovery_finds_per_year_index_pages_for_both_kinds_and_ignores_other_links()
    {
        var html = """
            <a href="/legislacion/superin/2026/indices/indcor.htm">2026</a>
            <a href="/legislacion/superin/1994/indices/indcor.htm">1994 - Aduanas</a>
            <a href="/legislacion/oficios/2025/indcor.htm">2025</a>
            <a href="../codigo/index.html">Código Tributario</a>
            <a href="index-Art95A.html">Consultas</a>
            <a href="/legislacion/oficios/2025/indcor.htm">2025 (duplicado)</a>
            """;

        var pages = SunatIndexParser.DiscoverIndexPages(html, new Uri("https://www.sunat.gob.pe/legislacion/general/index.html"));

        Assert.Equal(3, pages.Count);
        Assert.Contains(pages, p => p.Year == 2026 && p.Tipo == TipoNorma.ResolucionSuperintendencia);
        Assert.Contains(pages, p => p.Year == 1994 && p.Tipo == TipoNorma.ResolucionSuperintendencia);
        Assert.Contains(pages, p => p.Year == 2025 && p.Tipo == TipoNorma.Informe
            && p.Url.AbsoluteUri == "https://www.sunat.gob.pe/legislacion/oficios/2025/indcor.htm");
    }

    [Theory]
    [InlineData("https://www.sunat.gob.pe/legislacion/superin/2025/000401-2025.pdf", "superin-2025-000401-2025")]
    [InlineData("https://www.sunat.gob.pe/legislacion/superin/2025/indices/indcor.htm", "superin-2025-indices-indcor")]
    [InlineData("https://www.sunat.gob.pe/otra/ruta/Doc_1.PDF", "otra-ruta-doc-1")]
    public void NormaIds_are_deterministic_slugs_of_the_url(string url, string expected) =>
        Assert.Equal(expected, NormaIds.FromUrl(new Uri(url)));
}
