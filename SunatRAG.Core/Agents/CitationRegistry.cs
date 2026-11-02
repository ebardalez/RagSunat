using System.Text.RegularExpressions;
using SunatRAG.Core.Retrieval;

namespace SunatRAG.Core.Agents;

/// <summary>A source the answer cited, resolved by the APP (never by the model) from the number the model wrote.</summary>
public sealed record Citation(
    int Number,
    string Cita,
    string DocumentId,
    int PageStart,
    int PageEnd,
    string Url,
    string Snippet)
{
    /// <summary>PDF viewers honour "#page=N": the link opens on the cited page.</summary>
    public string PageUrl => string.IsNullOrEmpty(Url) ? "" : $"{Url}#page={PageStart}";
}

/// <param name="Valid">Numbers the model cited that exist in the registry, ascending.</param>
/// <param name="Invalid">Numbers the model cited that were never shown to it: invented citations. Surface them, never hide them.</param>
public sealed record CitationResolution(IReadOnlyList<Citation> Valid, IReadOnlyList<int> Invalid);

/// <summary>
/// Conversation-wide numbering of retrieved chunks: chunk -> "[n]".
///
/// Why it exists: the model cites by number ("[2]"), and a conversation can contain several searches (the model may search twice for one
/// question, and follow-up questions search again). If every tool result were numbered from 1, "[1]" would mean different chunks in
/// different places. The registry hands out one stable number per chunk for the life of the session (one Blazor circuit), so:
///   - the same chunk found twice keeps its number;
///   - the answer's "[n]" always resolves to exactly one chunk;
///   - the model can never mint a URL or a page number: it only ever echoes numbers we gave it.
/// Thread-safe: the tool is invoked from the function-invocation loop, the UI reads from the Blazor renderer.
/// </summary>
public sealed partial class CitationRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<string, int> _numberByChunk = new(StringComparer.Ordinal);
    private readonly List<RetrievedChunk> _chunks = [];

    public int Count
    {
        get { lock (_gate) return _chunks.Count; }
    }

    /// <summary>Returns the chunk's number, assigning the next one the first time the chunk is seen.</summary>
    public int Register(RetrievedChunk chunk)
    {
        lock (_gate)
        {
            if (_numberByChunk.TryGetValue(chunk.Record.ChunkId, out var existing)) return existing;
            _chunks.Add(chunk);
            var number = _chunks.Count;
            _numberByChunk[chunk.Record.ChunkId] = number;
            return number;
        }
    }

    /// <summary>Finds "[3]", "[1][4]" and "[2, 5]" in an answer and resolves them.</summary>
    public CitationResolution Resolve(string answer)
    {
        var cited = new SortedSet<int>();
        foreach (Match m in CitationPattern().Matches(answer))
            foreach (Match n in Digits().Matches(m.Groups[1].Value))
                if (int.TryParse(n.Value, out var value)) cited.Add(value);

        var valid = new List<Citation>();
        var invalid = new List<int>();
        lock (_gate)
        {
            foreach (var number in cited)
            {
                if (number < 1 || number > _chunks.Count) { invalid.Add(number); continue; }
                var r = _chunks[number - 1].Record;
                valid.Add(new Citation(number, r.Cita(), r.DocumentId, r.PageStart, r.PageEnd, r.Url, Snippet(r.Text)));
            }
        }

        return new CitationResolution(valid, invalid);
    }

    public void Clear()
    {
        lock (_gate)
        {
            _chunks.Clear();
            _numberByChunk.Clear();
        }
    }

    private static string Snippet(string text)
    {
        var flat = WhiteSpace().Replace(text, " ").Trim();
        return flat.Length <= 240 ? flat : flat[..240] + "…";
    }

    // "[1]", "[1, 2]", "[1;2]". Deliberately not "[1-3]": a range is ambiguous and the instructions ask for separate brackets.
    [GeneratedRegex(@"\[(\d{1,3}(?:\s*[,;]\s*\d{1,3})*)\]")]
    private static partial Regex CitationPattern();

    [GeneratedRegex(@"\d+")]
    private static partial Regex Digits();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhiteSpace();
}
