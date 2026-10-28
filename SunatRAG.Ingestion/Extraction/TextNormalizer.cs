using System.Text;
using System.Text.RegularExpressions;

namespace SunatRAG.Ingestion.Extraction;

/// <summary>One visual paragraph (block between blank lines) with the page it came from.</summary>
/// <param name="FirstLine">Raw first line, kept apart because heading detection must look at it BEFORE lines are joined.</param>
public sealed record Paragraph(int Page, string Text, string FirstLine);

/// <summary>
/// PdfPig gives "soft-wrapped" text: one line per visual line, blank line between paragraphs.
/// This turns pages into clean paragraphs and removes the electronic-signature boilerplate that SUNAT stamps
/// on every PDF (otherwise it ends up in the vector index and pollutes retrieval).
/// </summary>
public static class TextNormalizer
{
    private static readonly Regex BlockSeparator = new(@"\n[ \t]*\n+", RegexOptions.Compiled);
    private static readonly Regex Spaces = new(@"[ \t]{2,}", RegexOptions.Compiled);

    private static readonly Regex[] Boilerplate =
    [
        new(@"^\d+\s*/\s*\d+$", RegexOptions.Compiled),                                  // "2/3" page marker
        new(@"^Esta es una representaci[oó]n impresa", RegexOptions.Compiled),
        new(@"^URL:\s*https?://", RegexOptions.Compiled),
        new(@"^CVD:\s*[\d ]+$", RegexOptions.Compiled),
        new(@"^Documento electr[oó]nico firmado digitalmente", RegexOptions.Compiled),
        new(@"^Firmado digitalmente por:", RegexOptions.Compiled),
        new(@"^V[º°o]\s*B[º°o]\b", RegexOptions.Compiled),
        new(@"\bREG\.\s*\d+$", RegexOptions.Compiled),                                   // "7T2000 REG. 1082"
        new(@"^[a-z]{2,5}(/[a-z]{2,5}){1,3}\s", RegexOptions.Compiled)                    // drafter initials "ctc/slls/amf CT00251 ..."
    ];

    private static readonly Regex SignatureTimestamp =
        new(@"\d{2}/\d{2}/\d{4}\s+\d{2}:\d{2}(:\d{2})?$", RegexOptions.Compiled);

    public static IReadOnlyList<Paragraph> ToParagraphs(IReadOnlyList<PageText> pages)
    {
        var result = new List<Paragraph>();
        foreach (var page in pages)
        {
            var raw = Clean(page.Text);
            foreach (var block in BlockSeparator.Split(raw))
            {
                var lines = block.Split('\n')
                    .Select(l => l.Trim())
                    .Where(l => l.Length > 0)
                    .ToArray();
                if (lines.Length == 0) continue;

                var text = Spaces.Replace(JoinLines(lines), " ");
                if (IsBoilerplate(text, lines.Length)) continue;

                result.Add(new Paragraph(page.Number, text, lines[0]));
            }
        }

        return result;
    }

    internal static string Clean(string text) =>
        text.Normalize(NormalizationForm.FormC)            // PDFs often store "ó" as "o" + combining accent
            .Replace("\r\n", "\n")
            .Replace('\r', '\n')
            .Replace('\u2028', '\n').Replace('\u2029', '\n').Replace('\u0085', '\n').Replace('\v', '\n').Replace('\f', '\n')
            .Replace("\u00AD", "")                         // soft hyphen
            .Replace("\u200B", "")                         // zero-width space
            .Replace('\u00A0', ' ');                       // no-break space

    /// <summary>
    /// Joins soft-wrapped lines. The hyphen rule matters in legal text:
    /// "compen-/sación" is one word (drop the hyphen) but "000003-/2022-SUNAT" is one number (keep the hyphen, no space).
    /// </summary>
    internal static string JoinLines(IReadOnlyList<string> lines)
    {
        var sb = new StringBuilder(lines[0]);
        for (var i = 1; i < lines.Count; i++)
        {
            var next = lines[i];
            if (sb.Length > 1 && sb[^1] == '-')
            {
                var before = sb[^2];
                var first = next[0];
                if (char.IsDigit(before) && char.IsLetterOrDigit(first))
                {
                    sb.Append(next);
                    continue;
                }

                if (char.IsLetter(before) && char.IsLower(first))
                {
                    sb.Length--;
                    sb.Append(next);
                    continue;
                }
            }

            sb.Append(' ').Append(next);
        }

        return sb.ToString();
    }

    internal static bool IsBoilerplate(string text, int lineCount)
    {
        if (text.All(c => !char.IsLetter(c)) && text.Length <= 2) return true;      // stray "." or "-"
        if (Boilerplate.Any(rx => rx.IsMatch(text))) return true;
        // Signature stamp: short block ending with "31/12/2025 10:31:40".
        return lineCount <= 8 && text.Length < 160 && SignatureTimestamp.IsMatch(text);
    }
}
