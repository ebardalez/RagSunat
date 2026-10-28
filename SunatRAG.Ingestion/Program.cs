using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry.Trace;
using SunatRAG.Core.Composition;
using SunatRAG.Ingestion.Commands;
using SunatRAG.Ingestion.Scraping;

// ContentRoot = output folder so appsettings*.json are found no matter where `dotnet run` is invoked from.
// (Relative paths such as Scraping:DataDirectory still resolve against the CURRENT directory: run from the repo root.)
var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
    EnvironmentName = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") ?? Environments.Development
});
builder.Services.AddSunatAi(builder.Configuration);
builder.Services.AddSunatScraping(builder.Configuration);
builder.Services.AddOpenTelemetry()
    .WithTracing(t => t.AddSource(AiServiceCollectionExtensions.TelemetrySourceName).AddConsoleExporter());

using var host = builder.Build();

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true; // finish the current download cleanly; the manifest keeps what is done
    cts.Cancel();
};

var command = args.FirstOrDefault() ?? "smoke";
try
{
    return command switch
    {
        "smoke" => await SmokeCommand.RunAsync(host.Services, cts.Token),
        "scrape" => await ScrapeCommand.RunAsync(host.Services, args[1..], cts.Token),
        _ => PrintUsage()
    };
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Cancelado por el usuario.");
    return 130;
}

static int PrintUsage()
{
    Console.Error.WriteLine("Comandos:");
    Console.Error.WriteLine("  smoke    prueba de conectividad con Ollama (Fase 0)");
    Console.Error.WriteLine($"  {ScrapeCommand.Usage.Replace("Uso: dotnet run --project SunatRAG.Ingestion -- ", "")}");
    return 1;
}
