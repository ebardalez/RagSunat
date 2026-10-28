using SunatRAG.Ingestion.Extraction;

namespace SunatRAG.Tests.Extraction;

public class TokenAwareChunkerTests
{
    /// <summary>1 word = 1 token: makes every size assertion exact and independent of any model file.</summary>
    private sealed class WordCounter : ITokenCounter
    {
        public int Count(string text) => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
    }

    private static TokenAwareChunker Chunker(int target = 50, int overlap = 10, int minChunk = 15) =>
        new(new WordCounter(), new ChunkingOptions { TargetTokens = target, OverlapTokens = overlap, MinChunkTokens = minChunk });

    /// <summary>"S3 w w w w w w w." = exactly <paramref name="words"/> words, starts uppercase so the sentence splitter cuts before it.</summary>
    private static string Sentence(int id, int words = 8) =>
        $"S{id} " + string.Join(' ', Enumerable.Repeat("w", words - 1)) + ".";

    private static string Paragraph(int firstId, int sentences, int words = 8) =>
        string.Join(' ', Enumerable.Range(firstId, sentences).Select(i => Sentence(i, words)));

    private static Paragraph P(string text, int page = 1) => new(page, text, text.Split('\n')[0]);

    private static Section S(string? heading, params Paragraph[] paragraphs) => new(heading, paragraphs);

    [Fact]
    public void Small_sections_are_merged_up_to_the_target_and_never_split()
    {
        var sections = new[]
        {
            S("Artículo 1", P(Paragraph(1, 2, 10))),   // 20 words
            S("Artículo 2", P(Paragraph(10, 2, 10))),  // 20
            S("Artículo 3", P(Paragraph(20, 2, 10)))   // 20
        };

        var chunks = Chunker().Chunk(sections);

        Assert.Equal(2, chunks.Count);
        Assert.Equal(40, chunks[0].TokenCount);
        Assert.Equal("Artículo 1", chunks[0].Heading);
        Assert.Equal(20, chunks[1].TokenCount);
        Assert.Equal("Artículo 3", chunks[1].Heading);
    }

    [Fact]
    public void Oversized_section_is_cut_at_sentence_boundaries_within_the_target()
    {
        var sections = new[] { S("Artículo 1", P(Paragraph(1, 5)), P(Paragraph(6, 5)), P(Paragraph(11, 5))) }; // 15 sentences x 8 = 120 words

        var chunks = Chunker().Chunk(sections);

        Assert.True(chunks.Count >= 3);
        Assert.All(chunks, c => Assert.True(c.TokenCount <= 50, $"chunk of {c.TokenCount} tokens"));
        Assert.All(chunks, c => Assert.EndsWith(".", c.Text));          // never cut mid-sentence
        Assert.All(chunks, c => Assert.Equal("Artículo 1", c.Heading));
    }

    [Fact]
    public void Consecutive_chunks_of_one_section_overlap_by_whole_sentences_within_the_budget()
    {
        var sections = new[] { S("Artículo 1", P(Paragraph(1, 15))) };

        var chunks = Chunker(target: 50, overlap: 10).Chunk(sections);

        // overlap budget 10 tokens, sentences of 8 words: exactly ONE sentence is carried over.
        for (var i = 1; i < chunks.Count; i++)
        {
            var previousLastSentence = chunks[i - 1].Text.Split(". ").Last().TrimEnd('.');
            Assert.StartsWith(previousLastSentence, chunks[i].Text);
        }
    }

    [Fact]
    public void Overlap_is_not_carried_across_section_boundaries()
    {
        var sections = new[]
        {
            S("Artículo 1", P(Paragraph(1, 10))),     // 80 words: oversized
            S("Artículo 2", P(Paragraph(100, 10)))    // 80 words: oversized
        };

        var chunks = Chunker().Chunk(sections);

        var firstOfSecond = chunks.First(c => c.Heading == "Artículo 2");
        Assert.StartsWith("S100 ", firstOfSecond.Text);
    }

    [Fact]
    public void Small_leftover_is_absorbed_by_the_next_oversized_section_instead_of_standing_alone()
    {
        var sections = new[]
        {
            S(null, P("RESOLUCIÓN N.° 1 Lima, 31 de diciembre")),  // ~7 words: below MinChunkTokens
            S("CONSIDERANDO", P(Paragraph(1, 15)))                 // 120 words: oversized
        };

        var chunks = Chunker().Chunk(sections);

        Assert.StartsWith("RESOLUCIÓN N.° 1", chunks[0].Text);
        Assert.Contains("S1 ", chunks[0].Text);
        Assert.All(chunks, c => Assert.True(c.TokenCount > 15));
    }

    [Fact]
    public void A_big_leftover_is_flushed_on_its_own_before_an_oversized_section()
    {
        var sections = new[]
        {
            S("Artículo 1", P(Paragraph(1, 4))),      // 32 words: above MinChunkTokens(15)
            S("Artículo 2", P(Paragraph(10, 10)))     // oversized
        };

        var chunks = Chunker().Chunk(sections);

        Assert.Equal(32, chunks[0].TokenCount);
        Assert.Equal("Artículo 1", chunks[0].Heading);
    }

    [Fact]
    public void Page_range_spans_the_pages_of_the_paragraphs_in_the_chunk()
    {
        var sections = new[] { S("Artículo 1", P(Paragraph(1, 2), page: 3), P(Paragraph(10, 2), page: 4)) };

        var chunk = Chunker().Chunk(sections).Single();

        Assert.Equal(3, chunk.PageStart);
        Assert.Equal(4, chunk.PageEnd);
    }

    [Fact]
    public void A_single_sentence_longer_than_the_target_is_cut_by_words_and_loses_nothing()
    {
        var words = Enumerable.Range(1, 200).Select(i => $"p{i}").ToArray();
        var sections = new[] { S("Artículo 1", P(string.Join(' ', words))) };

        var chunks = Chunker().Chunk(sections);

        Assert.All(chunks, c => Assert.True(c.TokenCount <= 50));
        var covered = chunks.SelectMany(c => c.Text.Split(' ')).ToHashSet();
        Assert.All(words, w => Assert.Contains(w, covered));
    }

    [Fact]
    public void Chunking_is_deterministic()
    {
        var sections = new[] { S("Artículo 1", P(Paragraph(1, 30))), S("Artículo 2", P(Paragraph(100, 3))) };

        var a = Chunker().Chunk(sections);
        var b = Chunker().Chunk(sections);

        Assert.Equal(a.Select(c => c.Text), b.Select(c => c.Text));
    }

    [Fact]
    public void Empty_input_yields_no_chunks()
    {
        Assert.Empty(Chunker().Chunk([]));
    }
}
