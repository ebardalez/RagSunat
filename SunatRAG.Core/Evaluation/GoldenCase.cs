using System.Text.Json;
using System.Text.Json.Serialization;

namespace SunatRAG.Core.Evaluation;

public enum GoldenKind
{
    /// <summary>Well-phrased question whose answer is in the index.</summary>
    Answerable,

    /// <summary>Same, but written the way a taxpayer talks (vocabulary mismatch with the norm).</summary>
    Colloquial,

    /// <summary>Nothing to do with Peruvian tax: the agent must decline.</summary>
    OutOfScope,

    /// <summary>In-domain but verified ABSENT from the indexed corpus: the agent must say it did not find it, not answer from memory.</summary>
    NotInIndex,

    /// <summary>Tries to override the instructions or extract the system prompt.</summary>
    Injection
}

/// <summary>
/// One line of eval/golden.jsonl.
///
/// "Evidence" is how relevance is judged, and it is the key design choice of the set: a retrieved chunk is answer-bearing when its text
/// contains any evidence string. That is robust to re-chunking (chunk ids change, text does not) and measures what RAG needs
/// (the sentence that answers is in the prompt), not just "the right document was somewhere in the top-k".
/// "ExpectedDocuments" is the looser, document-level view (any-of).
/// "AnswerFacts" are strings (any-of) the final answer must contain.
/// "Rewrite" is the query a good model would send to the search tool; evaluating raw question vs rewrite quantifies what query rewriting buys.
/// </summary>
public sealed record GoldenCase(
    string Id,
    GoldenKind Kind,
    string Question,
    string? Rewrite,
    IReadOnlyList<string> ExpectedDocuments,
    IReadOnlyList<string> Evidence,
    IReadOnlyList<string> AnswerFacts,
    string? Note = null)
{
    public bool ExpectsAnswer => Kind is GoldenKind.Answerable or GoldenKind.Colloquial;

    public string RewrittenOrRaw => string.IsNullOrWhiteSpace(Rewrite) ? Question : Rewrite;

    public bool IsEvidence(string chunkText) => EvalText.ContainsAny(EvalText.Fold(chunkText), Evidence);
}

public static class GoldenSet
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        ReadCommentHandling = JsonCommentHandling.Skip
    };

    /// <summary>JSON Lines: one case per line; blank lines and lines starting with '#' are ignored.</summary>
    public static IReadOnlyList<GoldenCase> Load(string path)
    {
        var cases = new List<GoldenCase>();
        var lineNumber = 0;
        foreach (var line in File.ReadLines(path))
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith('#')) continue;

            try
            {
                cases.Add(JsonSerializer.Deserialize<GoldenCase>(line, Options)
                          ?? throw new InvalidDataException("línea vacía"));
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException($"{path}:{lineNumber}: JSON inválido ({ex.Message})", ex);
            }
        }

        var duplicate = cases.GroupBy(c => c.Id, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null) throw new InvalidDataException($"{path}: id duplicado '{duplicate.Key}'.");
        return cases;
    }
}
