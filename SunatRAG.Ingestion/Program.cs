using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry.Trace;
using SunatRAG.Core.Composition;
using SunatRAG.Ingestion.Commands;
using SunatRAG.Ingestion.Extraction;
using SunatRAG.Ingestion.Scraping;

var command = args.FirstOrDefault() ?? "smoke";

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
builder.Services.AddSunatExtraction(builder.Configuration);
builder.Services.AddSunatIndex(builder.Configuration);
builder.Services.AddSunatRetrieval(builder.Configuration);
builder.Services.AddSunatAgent(builder.Configuration);

// The console exporter prints every span (one per embedding batch during `index`): useful for `smoke`,
// noise for long runs. Set SUNATRAG_TRACE=1 to see the spans of any command.
var traceToConsole = command == "smoke" || Environment.GetEnvironmentVariable("SUNATRAG_TRACE") == "1";
builder.Services.AddOpenTelemetry()
    .WithTracing(t =>
    {
        t.AddSource(AiServiceCollectionExtensions.TelemetrySourceName);
        t.AddSource(SunatRAG.Core.Diagnostics.SunatTelemetry.RetrievalSourceName);
        if (traceToConsole) t.AddConsoleExporter();
    });

using var host = builder.Build();

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true; // finish the current batch cleanly; what is already stored is kept and skipped on the next run
    cts.Cancel();
};

try
{
    return command switch
    {
        "smoke" => await SmokeCommand.RunAsync(host.Services, cts.Token),
        "scrape" => await ScrapeCommand.RunAsync(host.Services, args[1..], cts.Token),
        "inspect" => await InspectCommand.RunAsync(host.Services, args[1..], cts.Token),
        "extract" => await ExtractCommand.RunAsync(host.Services, args[1..], cts.Token),
        "index" => await IndexCommand.RunAsync(host.Services, args[1..], cts.Token),
        "retrieve" => await RetrieveCommand.RunAsync(host.Services, args[1..], cts.Token),
        "ask" => await AskCommand.RunAsync(host.Services, args[1..], cts.Token),
        "eval" => await EvalCommand.RunAsync(host.Services, args[1..], cts.Token),
        _ => PrintUsage()
    };
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Cancelado por el usuario.");
    return 130;
}
catch (HttpRequestException ex)
{
    Console.Error.WriteLine($"No se pudo contactar con el servidor: {ex.Message}");
    Console.Error.WriteLine("Revisa que la máquina de Ollama esté encendida y accesible (Ai:Ollama:Endpoint). Lo ya indexado se conserva: vuelve a ejecutar el mismo comando para continuar.");
    return 2;
}
catch (FileNotFoundException ex)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}

static int PrintUsage()
{
    Console.Error.WriteLine("Comandos:");
    Console.Error.WriteLine("  smoke    prueba de conectividad con Ollama (Fase 0)");
    Console.Error.WriteLine($"  {ScrapeCommand.Usage.Replace("Uso: dotnet run --project SunatRAG.Ingestion -- ", "")}");
    Console.Error.WriteLine($"  {ExtractCommand.Usage.Replace("Uso: dotnet run --project SunatRAG.Ingestion -- ", "")}");
    Console.Error.WriteLine($"  {IndexCommand.Usage.Replace("Uso: dotnet run --project SunatRAG.Ingestion -- ", "")}");
    Console.Error.WriteLine($"  {RetrieveCommand.Usage.Replace("Uso: dotnet run --project SunatRAG.Ingestion -- ", "")}");
    Console.Error.WriteLine($"  {AskCommand.Usage.Replace("Uso: dotnet run --project SunatRAG.Ingestion -- ", "")}");
    Console.Error.WriteLine($"  {EvalCommand.Usage.Replace("Uso: dotnet run --project SunatRAG.Ingestion -- ", "")}");
    return 1;
}
