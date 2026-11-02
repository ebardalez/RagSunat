using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace SunatRAG.Core.Evaluation;

/// <summary>
/// Text normalisation shared by every evaluation check. Both sides of a comparison (the evidence string from the golden set and the
/// chunk or answer text) go through <see cref="Fold"/>, so "Artículo 6" == "articulo 6", "5 500,00" == "5,500" == "5500" and
/// double spaces or line breaks from the PDF never cause a false miss.
/// </summary>
public static partial class EvalText
{
    public static string Fold(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";

        var decomposed = text.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var ch in decomposed)
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
                sb.Append(char.ToLowerInvariant(ch));

        var collapsed = Whitespace().Replace(sb.ToString().Normalize(NormalizationForm.FormC), " ").Trim();
        // "5 500", "5.500" and "5,500" are all one number: drop a separator that sits between a digit and exactly three more digits.
        return ThousandsSeparator().Replace(collapsed, "");
    }

    /// <summary>True when the (already folded) haystack contains any needle, folding each needle first.</summary>
    public static bool ContainsAny(string foldedHaystack, IEnumerable<string> needles) =>
        needles.Any(n =>
        {
            var folded = Fold(n);
            return folded.Length > 0 && foldedHaystack.Contains(folded, StringComparison.Ordinal);
        });

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"(?<=\d)[ .,](?=\d{3}(?!\d))")]
    private static partial Regex ThousandsSeparator();
}
