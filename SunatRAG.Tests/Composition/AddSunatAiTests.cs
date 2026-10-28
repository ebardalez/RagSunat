using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SunatRAG.Core.Composition;
using SunatRAG.Core.Configuration;

namespace SunatRAG.Tests.Composition;

public class AddSunatAiTests
{
    private static Dictionary<string, string?> ValidSettings() => new()
    {
        ["Ai:Provider"] = "Ollama",
        ["Ai:Ollama:Endpoint"] = "http://ollama.test:11434",
        ["Ai:Ollama:ChatModel"] = "qwen3:8b",
        ["Ai:Ollama:EmbeddingModel"] = "bge-m3",
        ["Ai:Ollama:EmbeddingDimensions"] = "1024",
        ["Ai:Ollama:RequestTimeout"] = "00:10:00",
    };

    private static ServiceProvider Build(Dictionary<string, string?> settings)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging(); // UseLogging() needs an ILoggerFactory, which the host normally provides.
        services.AddSunatAi(config);
        return services.BuildServiceProvider();
    }

    // Options validation runs when IOptions<T>.Value is read (and at host start via ValidateOnStart).
    private static AiOptions ReadOptions(ServiceProvider sp) => sp.GetRequiredService<IOptions<AiOptions>>().Value;

    [Fact]
    public void Valid_configuration_binds_options()
    {
        using var sp = Build(ValidSettings());

        var ai = ReadOptions(sp);

        Assert.Equal("Ollama", ai.Provider);
        Assert.Equal("qwen3:8b", ai.Ollama.ChatModel);
        Assert.Equal("bge-m3", ai.Ollama.EmbeddingModel);
        Assert.Equal(1024, ai.Ollama.EmbeddingDimensions);
        Assert.Equal(TimeSpan.FromMinutes(10), ai.Ollama.RequestTimeout);
    }

    [Fact]
    public void Chat_client_and_embedding_generator_resolve_from_di()
    {
        using var sp = Build(ValidSettings());

        Assert.NotNull(sp.GetRequiredService<IChatClient>());
        Assert.NotNull(sp.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>());
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a uri")]
    public void Endpoint_must_be_an_absolute_uri(string endpoint)
    {
        var settings = ValidSettings();
        settings["Ai:Ollama:Endpoint"] = endpoint;
        using var sp = Build(settings);

        var ex = Assert.Throws<OptionsValidationException>(() => ReadOptions(sp));

        Assert.Contains("Endpoint", ex.Message);
    }

    [Theory]
    [InlineData("00:01:00")]
    [InlineData("00:04:59")]
    public void Request_timeout_below_five_minutes_is_rejected(string timeout)
    {
        var settings = ValidSettings();
        settings["Ai:Ollama:RequestTimeout"] = timeout;
        using var sp = Build(settings);

        var ex = Assert.Throws<OptionsValidationException>(() => ReadOptions(sp));

        Assert.Contains("RequestTimeout", ex.Message);
    }

    [Fact]
    public void Request_timeout_of_exactly_five_minutes_is_accepted()
    {
        var settings = ValidSettings();
        settings["Ai:Ollama:RequestTimeout"] = "00:05:00";
        using var sp = Build(settings);

        Assert.Equal(TimeSpan.FromMinutes(5), ReadOptions(sp).Ollama.RequestTimeout);
    }

    [Fact]
    public void Unknown_provider_fails_fast_at_registration()
    {
        var settings = ValidSettings();
        settings["Ai:Provider"] = "Nope";

        var ex = Assert.Throws<NotSupportedException>(() => Build(settings));

        Assert.Contains("Nope", ex.Message);
    }
}
