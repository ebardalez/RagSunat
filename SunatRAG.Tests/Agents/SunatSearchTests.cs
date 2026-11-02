using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using SunatRAG.Core.Agents;
using SunatRAG.Core.Configuration;
using SunatRAG.Core.Documents;
using SunatRAG.Core.Retrieval;
using static SunatRAG.Tests.Agents.AgentTestSupport;

namespace SunatRAG.Tests.Agents;

public class SunatSearchTests
{
    private readonly IChunkRetriever _retriever = Substitute.For<IChunkRetriever>();
    private readonly CitationRegistry _registry = new();

    private SunatSearch Create(double weak = 0.40) =>
        new(_retriever, _registry, Options.Create(new AgentOptions { WeakMatchDistance = weak }), NullLogger<SunatSearch>.Instance);

    private void Returns(RetrievalResult result) =>
        _retriever.RetrieveAsync(Arg.Any<RetrievalRequest>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(result));

    [Fact]
    public async Task Search_NumbersTheChunksAndRendersThemForTheModel()
    {
        Returns(Result("plazo", 0.31, Chunk(Record("a#0001", "a", "El plazo vence en marzo.", page: 2))));

        var outcome = await Create().SearchAsync("plazo", null, null, CancellationToken.None);

        Assert.False(outcome.Weak);
        Assert.StartsWith("Fragmentos para \"plazo\"", outcome.Text);
        Assert.Contains("[1] Resolución de Superintendencia N° a, pág. 2 (Artículo 1.- Plazo)", outcome.Text);
        Assert.Contains("El plazo vence en marzo.", outcome.Text);
    }

    [Fact]
    public async Task Search_KeepsNumbersStableAcrossSearches()
    {
        var a = Chunk(Record("a#0001", "a", "uno"));
        var b = Chunk(Record("b#0001", "b", "dos"));
        Returns(Result("q1", 0.3, a));
        var search = Create();
        await search.SearchAsync("q1", null, null, CancellationToken.None);

        Returns(Result("q2", 0.3, b, a)); // second search finds a new chunk first, then the old one
        var second = await search.SearchAsync("q2", null, null, CancellationToken.None);

        Assert.Equal([2, 1], second.Chunks.Select(c => c.Number));
        Assert.Contains("[2] ", second.Text);
        Assert.Contains("[1] ", second.Text);
    }

    [Fact]
    public async Task Search_NothingFound_TellsTheModelNotToAnswerFromOwnKnowledge()
    {
        Returns(Result("nada", null));

        var outcome = await Create().SearchAsync("nada", null, null, CancellationToken.None);

        Assert.True(outcome.Weak);
        Assert.Empty(outcome.Chunks);
        Assert.Contains("NO respondas con conocimiento propio", outcome.Text);
    }

    [Fact]
    public async Task Search_WeakMatch_AddsAWarningButStillReturnsTheChunks()
    {
        Returns(Result("coloquial", 0.44, Chunk(Record("a#0001", "a", "texto"))));

        var outcome = await Create(weak: 0.40).SearchAsync("coloquial", null, null, CancellationToken.None);

        Assert.True(outcome.Weak);
        Assert.StartsWith("AVISO: coincidencia débil (distancia 0.44)", outcome.Text);
        Assert.Contains("[1] ", outcome.Text);
    }

    [Fact]
    public async Task Search_PassesTipoAndYearFilters_AndKeepsTheSafeDefaults()
    {
        Returns(Result("q", 0.3));

        await Create().SearchAsync("q", "informe", 2025, CancellationToken.None);

        await _retriever.Received(1).RetrieveAsync(
            Arg.Is<RetrievalRequest>(r =>
                r.Filters!.Tipo == TipoNorma.Informe && r.Filters.Year == 2025
                && r.Filters.Categoria == Categoria.Normativa && !r.Filters.IncluirProyectos),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("resolucion", TipoNorma.ResolucionSuperintendencia)]
    [InlineData("Resolución", TipoNorma.ResolucionSuperintendencia)]
    [InlineData("INFORMES", TipoNorma.Informe)]
    [InlineData("informe", TipoNorma.Informe)]
    [InlineData("ley", null)]      // unknown: no filter rather than a filter that hides the right document
    [InlineData("", null)]
    [InlineData(null, null)]
    public void ParseTipo_IsTolerantAndNeverGuesses(string? raw, TipoNorma? expected) =>
        Assert.Equal(expected, SunatSearch.ParseTipo(raw));

    [Fact]
    public async Task Search_EmptyQuery_DoesNotCallTheRetriever()
    {
        var outcome = await Create().SearchAsync("   ", null, null, CancellationToken.None);

        Assert.StartsWith("Error:", outcome.Text);
        await _retriever.DidNotReceiveWithAnyArgs().RetrieveAsync(default!, default);
    }

    [Fact]
    public void AsTool_ExposesNameAndDescribedParametersToTheModel()
    {
        var tool = Create().AsTool();

        Assert.Equal("buscar_normativa", tool.Name);
        var schema = tool.JsonSchema.ToString();
        Assert.Contains("consulta", schema);
        Assert.Contains("tipo", schema);
        Assert.Contains("anio", schema);
        Assert.Contains("vocabulario normativo", schema); // the parameter description is the model's only documentation
    }
}
