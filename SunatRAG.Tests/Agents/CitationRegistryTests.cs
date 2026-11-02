using SunatRAG.Core.Agents;
using SunatRAG.Core.Retrieval;
using static SunatRAG.Tests.Agents.AgentTestSupport;

namespace SunatRAG.Tests.Agents;

public class CitationRegistryTests
{
    private static RetrievedChunk C(string id, string doc, int page = 1) => Chunk(Record(id, doc, $"texto de {id}", page));

    [Fact]
    public void Register_AssignsSequentialNumbers_AndKeepsTheNumberWhenTheSameChunkReturns()
    {
        var registry = new CitationRegistry();

        Assert.Equal(1, registry.Register(C("a#0001", "a")));
        Assert.Equal(2, registry.Register(C("b#0001", "b")));
        Assert.Equal(1, registry.Register(C("a#0001", "a"))); // found again by a second search
        Assert.Equal(2, registry.Count);
    }

    [Fact]
    public void Resolve_FindsSingleAdjacentAndCommaSeparatedCitations()
    {
        var registry = new CitationRegistry();
        foreach (var id in new[] { "a", "b", "c", "d" }) registry.Register(C($"{id}#0001", id));

        var result = registry.Resolve("El plazo es X [1]. Otro dato [2][4] y otro [3, 1].");

        Assert.Equal([1, 2, 3, 4], result.Valid.Select(c => c.Number));
        Assert.Empty(result.Invalid);
    }

    [Fact]
    public void Resolve_FlagsNumbersTheModelInvented()
    {
        var registry = new CitationRegistry();
        registry.Register(C("a#0001", "a"));

        var result = registry.Resolve("Dice algo [1] y algo inventado [7] y [0].");

        Assert.Equal([1], result.Valid.Select(c => c.Number));
        Assert.Equal([0, 7], result.Invalid);
    }

    [Fact]
    public void Resolve_BuildsTheCitationFromTheStoredRecord_NotFromAnythingTheModelWrote()
    {
        var registry = new CitationRegistry();
        registry.Register(Chunk(Record("x#0003", "superin-2025-000386-2025", "Artículo 6. Plazos para presentar la declaración.", page: 4)));

        var citation = Assert.Single(registry.Resolve("Ver [1] (https://evil.example/fake.pdf pág. 99).").Valid);

        Assert.Equal("superin-2025-000386-2025", citation.DocumentId);
        Assert.Equal(4, citation.PageStart);
        Assert.Equal("https://www.sunat.gob.pe/superin-2025-000386-2025.pdf#page=4", citation.PageUrl);
        Assert.Contains("pág. 4", citation.Cita);
    }

    [Fact]
    public void Resolve_IgnoresBracketsThatAreNotCitations()
    {
        var registry = new CitationRegistry();
        registry.Register(C("a#0001", "a"));

        var result = registry.Resolve("Según [Anexo] y [n] y [1-3] y [ ].");

        Assert.Empty(result.Valid);
        Assert.Empty(result.Invalid);
    }

    [Fact]
    public void Clear_RestartsNumberingAtOne()
    {
        var registry = new CitationRegistry();
        registry.Register(C("a#0001", "a"));

        registry.Clear();

        Assert.Equal(1, registry.Register(C("z#0001", "z")));
    }
}
