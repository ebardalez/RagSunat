using System.Text.RegularExpressions;

namespace SunatRAG.Core.Evaluation;

/// <summary>A numbered source the conversation showed to the model: what "[n]" in an answer refers to.</summary>
public sealed record GroundingSource(int Number, string Text);

/// <param name="Cited">Source numbers cited by the sentence.</param>
/// <param name="Overlap">Share of the sentence's content words (by 6-letter stem) that appear in the cited sources.</param>
public sealed record SentenceSupport(string Sentence, IReadOnlyList<int> Cited, double Overlap);

/// <param name="UngroundedNumbers">Numbers, dates or percentages written in the answer that appear in NO source shown to the model.</param>
/// <param name="UncitedClaims">Sentences that look like factual claims (digits or long) but carry no [n].</param>
/// <param name="WeakSupport">Cited sentences whose words mostly do not appear in the sources they cite.</param>
public sealed record GroundingReport(
    IReadOnlyList<string> UngroundedNumbers,
    IReadOnlyList<string> UncitedClaims,
    IReadOnlyList<SentenceSupport> WeakSupport,
    int SentencesChecked)
{
    public bool Clean => UngroundedNumbers.Count == 0 && UncitedClaims.Count == 0 && WeakSupport.Count == 0;
}

/// <summary>
/// A deterministic groundedness check: no model involved, so it is free, fast, repeatable and runs on every answer in production.
///
/// What it CAN catch (cheap, high-precision for tax text where numbers are the answer):
///   1. numbers/dates/percentages in the answer that no retrieved source contains (the classic hallucination: "18%", "15 días");
///   2. factual-looking sentences without a citation;
///   3. cited sentences that share almost no vocabulary with the source they cite (citation attached to the wrong claim).
/// What it CANNOT catch: a sentence that reuses the source's words but reverses its meaning ("sí está exonerado" vs "no está
/// exonerado"). That needs a semantic judge (<see cref="LlmGroundingJudge"/>), which is why that one exists as a measured experiment.
/// </summary>
public static partial class GroundednessChecker
{
    public const double MinOverlap = 0.5;

    private static readonly HashSet<string> Stopwords = new(StringComparer.Ordinal)
    {
        "para", "como", "pero", "porque", "este", "esta", "estos", "estas", "esto", "donde", "cuando", "entre", "sobre", "hasta", "desde",
        "segun", "tambien", "puede", "pueden", "debe", "deben", "cada", "otro", "otra", "otros", "otras", "solo", "tiene", "tienen",
        "sido", "siendo", "dicho", "dicha", "dichos", "dichas", "cual", "cuales", "sera", "seran", "asi", "mediante", "conforme",
        "respecto", "ademas", "ante", "bajo", "tras", "dentro", "fuera", "aquel", "aquella", "ellos", "ellas", "mismo", "misma"
    };

    public static GroundingReport Check(string answer, IReadOnlyList<GroundingSource> sources, string? allowedText = null)
    {
        var byNumber = sources.ToDictionary(s => s.Number, s => EvalText.Fold(s.Text));
        var allSourceText = string.Join(' ', byNumber.Values) + " " + EvalText.Fold(allowedText);
        var knownNumbers = NumberTokens(allSourceText);
        var allStems = Stems(allSourceText);

        var ungrounded = new SortedSet<string>(StringComparer.Ordinal);
        var uncited = new List<string>();
        var weak = new List<SentenceSupport>();
        var checkedCount = 0;

        foreach (var raw in SplitSentences(answer))
        {
            var folded = EvalText.Fold(raw);
            if (folded.Length < 12 || IsDisclaimer(folded)) continue;

            // Citation numbers and list markers are not claims: strip them before looking at digits and words.
            var cited = CitedNumbers(raw);
            var body = CitationBrackets().Replace(folded, " ");
            body = ListMarker().Replace(body, "");
            var refusal = AnswerChecks.LooksLikeRefusal(body);

            checkedCount++;
            var numbers = NumberTokens(body);
            if (!refusal)
                foreach (var n in numbers)
                    if (!IsKnown(n, knownNumbers)) ungrounded.Add(n);

            var words = ContentWords(body);
            if (cited.Count == 0)
            {
                if (!refusal && (numbers.Count > 0 || words.Count >= 8)) uncited.Add(Truncate(raw.Trim(), 140));
                continue;
            }

            if (words.Count < 3) continue;
            var citedStems = Stems(string.Join(' ', cited.Where(byNumber.ContainsKey).Select(n => byNumber[n])));
            var present = words.Count(w => citedStems.Contains(Stem(w)));
            var overlap = present / (double)words.Count;
            if (overlap < MinOverlap) weak.Add(new SentenceSupport(Truncate(raw.Trim(), 140), cited, overlap));
        }

        _ = allStems; // reserved: union-of-sources overlap, useful if per-citation overlap proves too strict
        return new GroundingReport([.. ungrounded], uncited, weak, checkedCount);
    }

    public static bool HasDisclaimer(string answer) => EvalText.Fold(answer).Contains("no constituye asesoria", StringComparison.Ordinal);

    private static bool IsDisclaimer(string folded) =>
        folded.Contains("caracter educativo", StringComparison.Ordinal) && folded.Contains("asesoria", StringComparison.Ordinal);

    private static IEnumerable<string> SplitSentences(string text) =>
        SentenceBoundary().Split(text).Select(s => s.Trim()).Where(s => s.Length > 0);

    private static List<int> CitedNumbers(string sentence)
    {
        var set = new SortedSet<int>();
        foreach (Match m in CitationBrackets().Matches(sentence))
            foreach (Match d in Digits().Matches(m.Value))
                if (int.TryParse(d.Value, out var n)) set.Add(n);
        return [.. set];
    }

    private static HashSet<string> NumberTokens(string folded)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in Number().Matches(folded))
        {
            set.Add(m.Value);
            // "5500,00" must also be known as "5500" and "00": the model may write the amount with or without decimals.
            foreach (var part in m.Value.Split('.', ','))
                if (part.Length > 0) set.Add(part);
        }

        return set;
    }

    private static bool IsKnown(string number, HashSet<string> known) =>
        known.Contains(number) || number.Split('.', ',').All(p => p.Length == 0 || known.Contains(p));

    private static List<string> ContentWords(string folded) =>
        Word().Matches(folded).Select(m => m.Value).Where(w => w.Length >= 4 && !Stopwords.Contains(w)).Distinct().ToList();

    private static HashSet<string> Stems(string folded) => ContentWords(folded).Select(Stem).ToHashSet(StringComparer.Ordinal);

    // Crude stemming by truncation: "gravada"/"gravado"/"gravadas" -> "gravad". Good enough for an overlap ratio.
    private static string Stem(string word) => word.Length <= 6 ? word : word[..6];

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    [GeneratedRegex(@"(?<=[.!?:;])\s+|\r?\n+")]
    private static partial Regex SentenceBoundary();

    [GeneratedRegex(@"\[(\d{1,3}(?:\s*[,;]\s*\d{1,3})*)\]")]
    private static partial Regex CitationBrackets();

    [GeneratedRegex(@"^\s*(?:\d{1,2}[.)]|[-*•])\s+")]
    private static partial Regex ListMarker();

    [GeneratedRegex(@"\d+")]
    private static partial Regex Digits();

    [GeneratedRegex(@"\d+(?:[.,]\d+)*")]
    private static partial Regex Number();

    [GeneratedRegex(@"[a-z0-9ñ]+")]
    private static partial Regex Word();
}
