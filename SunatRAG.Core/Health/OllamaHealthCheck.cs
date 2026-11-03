using System.Net.Http.Json;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using SunatRAG.Core.Composition;
using SunatRAG.Core.Configuration;

namespace SunatRAG.Core.Health;

public sealed class OllamaHealthCheck(IHttpClientFactory httpFactory, IOptions<AiOptions> options) : IHealthCheck
{
    private sealed record TagsResponse(List<TagModel>? Models);
    private sealed record TagModel(string Name);

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var ai = options.Value;
        if (ai.Provider != "Ollama")
            return HealthCheckResult.Healthy($"Provider '{ai.Provider}': Ollama check skipped.");

        var o = ai.Ollama;
        try
        {
            var http = httpFactory.CreateClient(AiServiceCollectionExtensions.OllamaHttpClientName);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(10)); // the ping must not inherit the 10-minute timeout

            var tags = await http.GetFromJsonAsync<TagsResponse>("api/tags", cts.Token);
            var pulled = tags?.Models?.Select(m => m.Name).ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];

            var missing = new[] { o.ChatModel, o.EmbeddingModel }
                .Where(m => !IsPulled(pulled, m))
                .ToArray();

            return missing.Length == 0
                ? HealthCheckResult.Healthy($"Ollama OK at {o.Endpoint}; models present.")
                : HealthCheckResult.Unhealthy(
                    $"Ollama reachable, but missing models: {string.Join(", ", missing)}. " +
                    $"Run: {string.Join("; ", missing.Select(m => $"ollama pull {m}"))}");
        }
        // The CALLER asked to stop (app shutdown, request aborted): that is not an unhealthy Ollama, so do not report it as one.
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        // Our own 10 s ping budget expired (a cancellation the caller did not ask for): the host is not answering.
        catch (OperationCanceledException ex)
        {
            return HealthCheckResult.Unhealthy($"Ollama at {o.Endpoint} did not answer within 10 s.", ex);
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy($"Cannot reach Ollama at {o.Endpoint}.", ex);
        }
    }

    // "bge-m3" is stored as "bge-m3:latest"
    private static bool IsPulled(HashSet<string> pulled, string model) =>
        pulled.Contains(model) || (!model.Contains(':') && pulled.Contains($"{model}:latest"));
}
