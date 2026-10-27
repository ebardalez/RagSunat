using System.Diagnostics;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using OpenTelemetry.Trace;
using SunatRAG.Core.Composition;
using SunatRAG.Core.Configuration;

// ContentRoot = output folder so appsettings*.json are found no matter where `dotnet run` is invoked from.
var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
    EnvironmentName = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") ?? Environments.Development
});
builder.Services.AddSunatAi(builder.Configuration);
builder.Services.AddOpenTelemetry()
    .WithTracing(t => t.AddSource(AiServiceCollectionExtensions.TelemetrySourceName).AddConsoleExporter());

using var host = builder.Build();
var sp = host.Services;
using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
var ct = cts.Token;

if ((args.FirstOrDefault() ?? "smoke") != "smoke")
{
    Console.Error.WriteLine("Usage: dotnet run --project SunatRAG.Ingestion -- smoke");
    return 1;
}

// 1) Health
var report = await sp.GetRequiredService<HealthCheckService>().CheckHealthAsync(ct);
foreach (var (name, e) in report.Entries) Console.WriteLine($"[health] {name}: {e.Status} - {e.Description}");
if (report.Status != HealthStatus.Healthy) return 2;

// 2) One chat call
var chat = sp.GetRequiredService<IChatClient>();
var sw = Stopwatch.StartNew();
var response = await chat.GetResponseAsync(
    "Responde en una sola frase: ¿qué es la SUNAT?", cancellationToken: ct);
Console.WriteLine($"[chat] {sw.Elapsed.TotalSeconds:F1}s  in={response.Usage?.InputTokenCount} out={response.Usage?.OutputTokenCount}");
Console.WriteLine(response.Text);

// 3) One embedding call
var embedder = sp.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>();
sw.Restart();
var vector = await embedder.GenerateVectorAsync(
    "Resolución de Superintendencia que aprueba el formulario virtual", cancellationToken: ct);
var expected = sp.GetRequiredService<IOptions<AiOptions>>().Value.Ollama.EmbeddingDimensions;
Console.WriteLine($"[embed] {sw.Elapsed.TotalMilliseconds:F0} ms  length={vector.Length} (expected {expected})");
return vector.Length == expected ? 0 : 3;
