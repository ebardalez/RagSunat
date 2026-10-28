using System.Net;
using System.Text;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using NSubstitute;
using SunatRAG.Core.Composition;
using SunatRAG.Core.Configuration;
using SunatRAG.Core.Health;

namespace SunatRAG.Tests.Health;

public class OllamaHealthCheckTests
{
    private const string Endpoint = "http://ollama.test:11434";

    /// <summary>Fake transport: lets us script what "Ollama" answers without a network.</summary>
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            LastRequest = request;
            return Task.FromResult(respond(request));
        }
    }

    private static (OllamaHealthCheck Check, StubHandler Handler, IHttpClientFactory Factory) Create(
        Func<HttpRequestMessage, HttpResponseMessage> respond, string provider = "Ollama")
    {
        var handler = new StubHandler(respond);
        var http = new HttpClient(handler) { BaseAddress = new Uri(Endpoint) };

        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(AiServiceCollectionExtensions.OllamaHttpClientName).Returns(http);

        var options = Options.Create(new AiOptions
        {
            Provider = provider,
            Ollama = new OllamaOptions { Endpoint = Endpoint, ChatModel = "qwen3:8b", EmbeddingModel = "bge-m3" }
        });

        return (new OllamaHealthCheck(factory, options), handler, factory);
    }

    private static HttpResponseMessage Tags(params string[] names)
    {
        var json = "{\"models\":[" + string.Join(",", names.Select(n => $"{{\"name\":\"{n}\"}}")) + "]}";
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }

    private static Task<HealthCheckResult> Run(OllamaHealthCheck check) =>
        check.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);

    [Fact]
    public async Task Healthy_when_both_models_are_pulled()
    {
        var (check, handler, _) = Create(_ => Tags("qwen3:8b", "bge-m3:latest"));

        var result = await Run(check);

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.Equal("/api/tags", handler.LastRequest!.RequestUri!.AbsolutePath);
    }

    [Theory]
    [InlineData("bge-m3")]
    [InlineData("bge-m3:latest")]
    public async Task Untagged_configured_model_matches_plain_or_latest(string pulledName)
    {
        var (check, _, _) = Create(_ => Tags("qwen3:8b", pulledName));

        Assert.Equal(HealthStatus.Healthy, (await Run(check)).Status);
    }

    [Fact]
    public async Task Model_name_comparison_ignores_case()
    {
        var (check, _, _) = Create(_ => Tags("QWEN3:8B", "BGE-M3:latest"));

        Assert.Equal(HealthStatus.Healthy, (await Run(check)).Status);
    }

    [Fact]
    public async Task Unhealthy_with_pull_hint_when_only_embedding_model_is_missing()
    {
        var (check, _, _) = Create(_ => Tags("qwen3:8b"));

        var result = await Run(check);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains("ollama pull bge-m3", result.Description);
        Assert.DoesNotContain("ollama pull qwen3:8b", result.Description);
    }

    [Fact]
    public async Task Unhealthy_listing_both_models_when_none_are_pulled()
    {
        var (check, _, _) = Create(_ => Tags());

        var result = await Run(check);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains("ollama pull qwen3:8b", result.Description);
        Assert.Contains("ollama pull bge-m3", result.Description);
    }

    [Fact]
    public async Task Explicit_tag_does_not_match_a_different_tag()
    {
        // Configured qwen3:8b, but only qwen3:4b is on the server.
        var (check, _, _) = Create(_ => Tags("qwen3:4b", "bge-m3"));

        var result = await Run(check);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains("qwen3:8b", result.Description);
    }

    [Fact]
    public async Task Unhealthy_when_models_field_is_null()
    {
        var (check, _, _) = Create(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"models\":null}", Encoding.UTF8, "application/json")
        });

        Assert.Equal(HealthStatus.Unhealthy, (await Run(check)).Status);
    }

    [Fact]
    public async Task Unhealthy_with_exception_when_server_is_unreachable()
    {
        var (check, _, _) = Create(_ => throw new HttpRequestException("No route to host"));

        var result = await Run(check);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.IsType<HttpRequestException>(result.Exception);
        Assert.Contains(Endpoint, result.Description);
    }

    [Fact]
    public async Task Unhealthy_when_server_returns_500()
    {
        var (check, _, _) = Create(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));

        Assert.Equal(HealthStatus.Unhealthy, (await Run(check)).Status);
    }

    [Fact]
    public async Task Skipped_without_any_http_call_when_provider_is_not_ollama()
    {
        var (check, handler, factory) = Create(_ => Tags(), provider: "AzureOpenAI");

        var result = await Run(check);

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.Equal(0, handler.Calls);
        factory.DidNotReceiveWithAnyArgs().CreateClient(default!);
    }
}
