using SunatRAG.Ingestion.Extraction;

namespace SunatRAG.Tests.Extraction;

public class LegalSectionSplitterTests
{
    private static Paragraph P(string text, int page = 1) => new(page, text.Replace('\n', ' '), text.Split('\n')[0]);

    private static IEnumerable<string?> Headings(params Paragraph[] paragraphs) =>
        LegalSectionSplitter.Split(paragraphs).Select(s => s.Heading);

    [Fact]
    public void Resolution_is_split_into_parts_articles_and_disposiciones()
    {
        var headings = Headings(
            P("RESOLUCIÓN DE SUPERINTENDENCIA N.° 000401-2025/SUNAT"),
            P("CONSIDERANDO:"),
            P("Que, mediante la Resolución anterior se aprobó."),
            P("SE RESUELVE:"),
            P("Artículo 1.- Objeto\nLa presente resolución regula."),
            P("Artículo 2.- Vigencia"),
            P("DISPOSICIÓN COMPLEMENTARIA FINAL"),
            P("Primera.- Vigencia\nEntra en vigor mañana."),
            P("Regístrese, comuníquese y publíquese."));

        Assert.Equal(
            [null, "CONSIDERANDO", "SE RESUELVE", "Artículo 1.- Objeto", "Artículo 2.- Vigencia",
             "DISPOSICIÓN COMPLEMENTARIA FINAL", "DISPOSICIÓN COMPLEMENTARIA FINAL > Primera.- Vigencia"],
            headings);
    }

    [Fact]
    public void Paragraphs_stay_with_the_section_that_opened_them()
    {
        var sections = LegalSectionSplitter.Split(
        [
            P("Artículo 1.- Objeto"),
            P("Primer párrafo del artículo."),
            P("Segundo párrafo del artículo."),
            P("Artículo 2.- Alcance")
        ]);

        Assert.Equal(3, sections[0].Paragraphs.Count);
        Assert.Single(sections[1].Paragraphs);
    }

    [Fact]
    public void Running_text_that_mentions_an_article_is_not_a_heading()
    {
        var headings = Headings(
            P("Artículo 1.- Objeto"),
            P("artículo 35 de la Ley del IGV establece lo siguiente."),
            P("Según el Artículo 5 de la norma citada."));

        Assert.Equal(["Artículo 1.- Objeto"], headings);
    }

    [Fact]
    public void Ordinals_are_headings_only_inside_disposiciones()
    {
        var headings = Headings(
            P("Artículo 1.- Objeto"),
            P("Primera.- esto es un ítem de lista, no una disposición"));

        Assert.Equal(["Artículo 1.- Objeto"], headings);
    }

    [Fact]
    public void Capitulo_prefixes_its_articles_and_a_new_part_resets_it()
    {
        var headings = Headings(
            P("CAPÍTULO I"),
            P("Artículo 1.- Objeto"),
            P("SE RESUELVE:"),
            P("Artículo 2.- Alcance"));

        Assert.Equal(["CAPÍTULO I", "CAPÍTULO I > Artículo 1.- Objeto", "SE RESUELVE", "Artículo 2.- Alcance"], headings);
    }

    [Fact]
    public void Annex_list_items_do_not_split_but_standalone_annex_headings_do_and_do_not_prefix_later_articles()
    {
        var headings = Headings(
            P("Artículo 1.- Modifica el procedimiento"),
            P("Anexo I: Solicitud de autorización de local temporalmente considerado"),
            P("ANEXO V"),
            P("Contenido del anexo quinto."),
            P("Artículo 2.- Incorpora disposiciones"));

        Assert.Equal(["Artículo 1.- Modifica el procedimiento", "ANEXO V", "Artículo 2.- Incorpora disposiciones"], headings);
    }

    [Fact]
    public void Informe_parts_are_recognised()
    {
        var headings = Headings(
            P("MATERIA:"),
            P("Se consulta si procede."),
            P("BASE LEGAL:"),
            P("- Texto Único Ordenado del Código Tributario."),
            P("ANÁLISIS:"),
            P("1. El artículo 27 establece."),
            P("CONCLUSIONES:"),
            P("La falta de presentación no es impedimento."));

        Assert.Equal(["MATERIA", "BASE LEGAL", "ANÁLISIS", "CONCLUSIONES"], headings);
    }

    [Fact]
    public void Long_heading_labels_are_truncated_with_an_ellipsis()
    {
        var heading = "Artículo 1.- " + new string('x', 200);

        var label = LegalSectionSplitter.Split([P(heading)]).Single().Heading!;

        Assert.Equal(90, label.Length);
        Assert.EndsWith("…", label);
    }
}
