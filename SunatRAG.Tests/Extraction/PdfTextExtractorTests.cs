using SunatRAG.Ingestion.Extraction;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace SunatRAG.Tests.Extraction;

internal static class PdfFactory
{
    public const string Sentence = "La presente resolucion aprueba el procedimiento general de prueba";

    /// <summary>One page per entry: null = blank page (what a scanned PDF looks like to a text extractor).</summary>
    public static byte[] Build(params string?[] pages)
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        foreach (var text in pages)
        {
            var page = builder.AddPage(UglyToad.PdfPig.Content.PageSize.A4);
            if (text is null) continue;

            var y = 780.0;
            foreach (var line in text.Split('\n'))
            {
                page.AddText(line, 11, new PdfPoint(50, y), font);
                y -= 16;
            }
        }

        return builder.Build();
    }
}

public class PdfTextExtractorTests
{
    private readonly PdfTextExtractor extractor = new();

    [Fact]
    public void Text_pdf_is_extracted_with_one_based_page_numbers()
    {
        var pdf = PdfFactory.Build(PdfFactory.Sentence, PdfFactory.Sentence + " dos");

        var result = extractor.Extract(pdf);

        Assert.Equal(ExtractionStatus.Ok, result.Status);
        Assert.Equal(2, result.PageCount);
        Assert.Equal([1, 2], result.Pages.Select(p => p.Number));
        Assert.Contains("procedimiento general", result.Pages[0].Text);
    }

    [Fact]
    public void Pdf_without_a_text_layer_is_flagged_NoText()
    {
        var result = extractor.Extract(PdfFactory.Build(null, null));

        Assert.Equal(ExtractionStatus.NoText, result.Status);
        Assert.Equal(2, result.EmptyPages);
    }

    [Fact]
    public void Mostly_empty_pdf_is_NoText_but_mostly_text_pdf_is_Ok_with_the_empty_pages_counted()
    {
        var mostlyEmpty = extractor.Extract(PdfFactory.Build(PdfFactory.Sentence, null, null));
        var mostlyText = extractor.Extract(PdfFactory.Build(PdfFactory.Sentence, PdfFactory.Sentence, null));

        Assert.Equal(ExtractionStatus.NoText, mostlyEmpty.Status);
        Assert.Equal(ExtractionStatus.Ok, mostlyText.Status);
        Assert.Equal(1, mostlyText.EmptyPages);
    }

    [Fact]
    public void Garbage_bytes_are_reported_as_Failed_instead_of_throwing()
    {
        var result = extractor.Extract("esto no es un pdf"u8.ToArray());

        Assert.Equal(ExtractionStatus.Failed, result.Status);
        Assert.False(string.IsNullOrEmpty(result.Error));
    }

    [Fact]
    public void Missing_file_is_reported_as_Failed()
    {
        var result = extractor.Extract(Path.Combine(Path.GetTempPath(), "no-existe-" + Guid.NewGuid().ToString("N") + ".pdf"));

        Assert.Equal(ExtractionStatus.Failed, result.Status);
    }

    [Fact]
    public void Cancellation_is_not_swallowed()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() => extractor.Extract(PdfFactory.Build(PdfFactory.Sentence), cts.Token));
    }
}
