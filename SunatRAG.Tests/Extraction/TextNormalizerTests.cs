using SunatRAG.Ingestion.Extraction;

namespace SunatRAG.Tests.Extraction;

public class TextNormalizerTests
{
    private static IReadOnlyList<Paragraph> Run(string pageText, int page = 1) =>
        TextNormalizer.ToParagraphs([new PageText(page, pageText)]);

    [Fact]
    public void Blank_lines_separate_paragraphs_and_soft_wrapped_lines_are_joined()
    {
        var paragraphs = Run("Que, mediante la Resolución\nde Superintendencia se aprobó;   \n\nQue, de conformidad\ncon el artículo 5;\n");

        Assert.Equal(2, paragraphs.Count);
        Assert.Equal("Que, mediante la Resolución de Superintendencia se aprobó;", paragraphs[0].Text);
        Assert.Equal("Que, de conformidad con el artículo 5;", paragraphs[1].Text);
    }

    [Fact]
    public void First_line_is_kept_raw_for_heading_detection()
    {
        var p = Run("Artículo 1.- Objeto\nLa presente resolución regula.").Single();

        Assert.Equal("Artículo 1.- Objeto", p.FirstLine);
        Assert.Equal("Artículo 1.- Objeto La presente resolución regula.", p.Text);
    }

    [Theory]
    [InlineData("Resolución de Oficina N.° 000003-\n2022-SUNAT/1V0000, señala", "Resolución de Oficina N.° 000003-2022-SUNAT/1V0000, señala")]  // number: keep hyphen, no space
    [InlineData("la compen-\nsación de la deuda", "la compensación de la deuda")]                             // word break: drop hyphen
    [InlineData("Decreto-\nLey N.° 25897", "Decreto- Ley N.° 25897")]                                        // uppercase next: real hyphen, keep + space
    public void Line_end_hyphens_are_handled_by_context(string input, string expected)
    {
        Assert.Equal(expected, Run(input).Single().Text);
    }

    [Fact]
    public void Decomposed_accents_are_normalized_to_NFC()
    {
        var decomposed = "Resoluci" + "o\u0301" + "n de Superintendencia";   // "o" + combining acute

        Assert.Equal("Resoluci\u00F3n de Superintendencia", Run(decomposed).Single().Text);
    }

    [Fact]
    public void Unicode_line_separators_and_nbsp_are_treated_as_whitespace()
    {
        var p = Run("Art\u00EDculo\u00A01.- Objeto\u2028La presente resoluci\u00F3n.").Single();

        Assert.Equal("Art\u00EDculo 1.- Objeto La presente resoluci\u00F3n.", p.Text);
        Assert.Equal("Art\u00EDculo 1.- Objeto", p.FirstLine);
    }
    [Fact]
    public void Electronic_signature_boilerplate_is_dropped_and_real_text_is_kept()
    {
        const string page = """
            2/3

            Sobre el particular, cabe mencionar que la compensación procede.

            Vº Bº

            7T2000
            REG. 1082

            ERNESTO JAVIER
            LOAYZA CAMACHO
            GERENTE
            31/12/2025 10:31:40

            Esta es una representación impresa cuya autenticidad puede ser contrastada
            con la representación imprimible localizada en la sede digital.

            URL: https://www.sunat.gob.pe/cl-ti-itinteroperabilidad/valida/verificacion
            CVD: 0157 7240 5974 7036

            Documento electrónico firmado digitalmente en el marco de la Ley N°27269.

            .

            Firmado digitalmente por:
            JOHANNA KARELLE RONDON
            INTENDENTE NACIONAL
            Fecha y hora: 31/12/2025 12:14

            ctc/slls/amf CT00251 y CT 00253 a 00257-2024 Impuesto a la Renta
            """;

        var paragraphs = Run(page);

        var only = Assert.Single(paragraphs);
        Assert.StartsWith("Sobre el particular", only.Text);
    }

    [Fact]
    public void Paragraphs_remember_their_page_number()
    {
        var paragraphs = TextNormalizer.ToParagraphs(
        [
            new PageText(1, "Primer párrafo de la página uno."),
            new PageText(2, "Segundo párrafo, ya en la página dos.")
        ]);

        Assert.Equal([1, 2], paragraphs.Select(p => p.Page));
    }
}
