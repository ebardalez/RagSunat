using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace SunatRAG.Ingestion.Extraction;

/// <summary>Text of one PDF page. <see cref="Number"/> is 1-based: it is what a citation prints.</summary>
public sealed record PageText(int Number, string Text);

public enum ExtractionStatus
{
    Ok,

    /// <summary>Scanned / image-only PDF: PdfPig found (almost) no text layer. OCR is out of scope: flag and skip.</summary>
    NoText,

    /// <summary>Corrupt, encrypted or otherwise unreadable.</summary>
    Failed
}

public sealed record ExtractionResult(
    ExtractionStatus Status,
    IReadOnlyList<PageText> Pages,
    int PageCount,
    int EmptyPages,
    string? Error = null)
{
    public static ExtractionResult Failure(string error) => new(ExtractionStatus.Failed, [], 0, 0, error);
}

public sealed class PdfExtractionOptions
{
    /// <summary>A page with fewer non-whitespace characters than this counts as "no text layer".</summary>
    public int MinCharsPerTextPage { get; init; } = 25;

    /// <summary>If less than this share of pages has text, the whole PDF is flagged <see cref="ExtractionStatus.NoText"/>.</summary>
    public double MinTextPageRatio { get; init; } = 0.5;

    /// <summary>true = PdfPig's layout analysis (visual reading order); false = raw content-stream order (page.Text).</summary>
    public bool UseContentOrder { get; init; } = true;
}

/// <summary>
/// Thin wrapper over PdfPig. Why a wrapper: the rest of the pipeline only knows "pages of text" and a status,
/// so the PDF library can be swapped (or an OCR step added later) without touching the chunker.
/// </summary>
public sealed class PdfTextExtractor(PdfExtractionOptions options)
{
    public PdfTextExtractor() : this(new PdfExtractionOptions()) { }

    public ExtractionResult Extract(string path, CancellationToken ct = default)
    {
        try
        {
            using var pdf = PdfDocument.Open(path);
            return Extract(pdf, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ExtractionResult.Failure($"{ex.GetType().Name}: {ex.Message}");
        }
    }

    public ExtractionResult Extract(byte[] content, CancellationToken ct = default)
    {
        try
        {
            using var pdf = PdfDocument.Open(content);
            return Extract(pdf, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ExtractionResult.Failure($"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private ExtractionResult Extract(PdfDocument pdf, CancellationToken ct)
    {
        var pages = new List<PageText>(pdf.NumberOfPages);
        var empty = 0;

        foreach (var page in pdf.GetPages())
        {
            ct.ThrowIfCancellationRequested();

            // ContentOrderTextExtractor reads in visual order (columns, tables) instead of content-stream order.
            var text = options.UseContentOrder
                ? ContentOrderTextExtractor.GetText(page, addDoubleNewline: true)
                : page.Text;
            var visible = text.Count(c => !char.IsWhiteSpace(c));
            if (visible < options.MinCharsPerTextPage) empty++;

            pages.Add(new PageText(page.Number, text));
        }

        var count = pages.Count;
        var textPages = count - empty;
        var status = count == 0 || (double)textPages / count < options.MinTextPageRatio
            ? ExtractionStatus.NoText
            : ExtractionStatus.Ok;

        return new ExtractionResult(status, pages, count, empty);
    }
}
