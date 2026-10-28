using System.Text;
using System.Text.RegularExpressions;

namespace SunatRAG.Ingestion.Extraction;

public sealed record ChunkingOptions
{
    public const string SectionName = "Chunking";

    /// <summary>Soft ceiling per chunk, in embedding-model tokens.</summary>
    public int TargetTokens { get; init; } = 500;

    /// <summary>Tokens repeated at the start of the next chunk, ONLY when a long section is cut mid-way.</summary>
    public int OverlapTokens { get; init; } = 80;

    /// <summary>Leftover chunks smaller than this are merged into the next oversized section instead of standing alone.</summary>
    public int MinChunkTokens { get; init; } = 120;

    /// <summary>Path to bge-m3's sentencepiece.bpe.model. Relative paths resolve against the current directory.</summary>
    public string TokenizerModelPath { get; init; } = "data/models/sentencepiece.bpe.model";

    /// <summary>Allow the chars/3.5 estimate when the model file is missing. Dev only: the token budget becomes a guess.</summary>
    public bool AllowApproximateTokenizer { get; init; }
}

public sealed record TextChunk(string Text, int TokenCount, int PageStart, int PageEnd, string? Heading);

/// <summary>
/// Packs legal sections into ~Target-token chunks.
///  - A section that fits is never split; small neighbours are merged until Target is reached (breaks only at section boundaries).
///  - A section larger than Target is cut at SENTENCE boundaries with Overlap tokens carried into the next chunk.
///  - Overlap is never carried across a section boundary: the previous article is not context for the next one.
/// Why sentence units: with paragraph units (100-250 tokens) an 80-token overlap would almost always be zero.
/// </summary>
public sealed class TokenAwareChunker(ITokenCounter counter, ChunkingOptions options)
{
    private sealed record Unit(string Text, int Tokens, int Page, string? Heading, bool StartsParagraph);

    private static readonly Regex SentenceBreak = new(
        @"(?<=[.;:!?])\s+(?=[A-ZÁÉÍÓÚÑ¿¡“""(\d])", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public IReadOnlyList<TextChunk> Chunk(IReadOnlyList<Section> sections)
    {
        var target = options.TargetTokens;
        var chunks = new List<TextChunk>();
        var current = new List<Unit>();
        var currentTokens = 0;
        var newUnits = 0; // units added since the last flush that are NOT carried-over overlap

        void Add(Unit u, bool carried = false)
        {
            current.Add(u);
            currentTokens += u.Tokens;
            if (!carried) newUnits++;
        }

        void Emit()
        {
            if (newUnits > 0) chunks.Add(Build(current));
        }

        void Flush()
        {
            Emit();
            current = [];
            currentTokens = 0;
            newUnits = 0;
        }

        void FlushWithOverlap(Unit next)
        {
            Emit();
            var carry = new List<Unit>();
            var carryTokens = 0;
            for (var i = current.Count - 1; i >= 0; i--)
            {
                if (carryTokens + current[i].Tokens > options.OverlapTokens) break;
                carry.Insert(0, current[i]);
                carryTokens += current[i].Tokens;
            }

            // Overlap + next unit must still respect the target.
            while (carry.Count > 0 && carryTokens + next.Tokens > target)
            {
                carryTokens -= carry[0].Tokens;
                carry.RemoveAt(0);
            }

            current = carry;
            currentTokens = carryTokens;
            newUnits = 0;
        }

        foreach (var section in sections)
        {
            var paragraphs = section.Paragraphs
                .Select(p => new Unit(p.Text, counter.Count(p.Text), p.Page, section.Heading, true))
                .ToList();
            var total = paragraphs.Sum(u => u.Tokens);

            if (total <= target)
            {
                if (currentTokens > 0 && currentTokens + total > target) Flush();
                foreach (var u in paragraphs) Add(u);
                continue;
            }

            // Oversized section: walk it sentence by sentence. A small leftover (preámbulo, "SE RESUELVE:" stub) is not worth
            // a chunk of its own: it becomes the opening of the first chunk of this section.
            if (currentTokens > options.MinChunkTokens) Flush();
            foreach (var u in paragraphs.SelectMany(ToSentenceUnits))
            {
                if (currentTokens > 0 && currentTokens + u.Tokens > target) FlushWithOverlap(u);
                Add(u);
            }

            Flush();
        }

        Flush();
        return chunks;
    }

    private IEnumerable<Unit> ToSentenceUnits(Unit paragraph)
    {
        var first = true;
        foreach (var sentence in SentenceBreak.Split(paragraph.Text))
        {
            var text = sentence.Trim();
            if (text.Length == 0) continue;

            var tokens = counter.Count(text);
            if (tokens <= options.TargetTokens)
            {
                yield return new Unit(text, tokens, paragraph.Page, paragraph.Heading, first);
                first = false;
                continue;
            }

            // A single "sentence" bigger than the target (long enumerations without periods): cut by words.
            foreach (var piece in SplitByWords(text, tokens))
            {
                yield return new Unit(piece, counter.Count(piece), paragraph.Page, paragraph.Heading, first);
                first = false;
            }
        }
    }

    private IEnumerable<string> SplitByWords(string text, int tokens)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var tokensPerWord = Math.Max(0.5, (double)tokens / words.Length);
        var wordsPerPiece = Math.Max(10, (int)(options.TargetTokens * 0.9 / tokensPerWord));
        for (var i = 0; i < words.Length; i += wordsPerPiece)
            yield return string.Join(' ', words.Skip(i).Take(wordsPerPiece));
    }

    private TextChunk Build(List<Unit> units)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < units.Count; i++)
        {
            if (i > 0) sb.Append(units[i].StartsParagraph ? "\n\n" : " ");
            sb.Append(units[i].Text);
        }

        var text = sb.ToString();
        return new TextChunk(
            text,
            counter.Count(text),
            units.Min(u => u.Page),
            units.Max(u => u.Page),
            units[0].Heading);
    }
}
