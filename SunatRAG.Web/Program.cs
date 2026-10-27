using Microsoft.Extensions.Diagnostics.HealthChecks;
using OpenTelemetry.Trace;
using SunatRAG.Core.Composition;
using SunatRAG.Web.Components;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddSunatAi(builder.Configuration);
builder.Services.AddOpenTelemetry()
    .WithTracing(t => t
        .AddSource(AiServiceCollectionExtensions.TelemetrySourceName)
        .AddConsoleExporter());

var app = builder.Build();

// Startup check: log loudly, don't crash (Ollama may be waking up on the other machine).
var report = await app.Services.GetRequiredService<HealthCheckService>().CheckHealthAsync();
var startupLog = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");
foreach (var (name, entry) in report.Entries)
    startupLog.Log(entry.Status == HealthStatus.Healthy ? LogLevel.Information : LogLevel.Error,
        "Health[{Name}] {Status}: {Description}", name, entry.Status, entry.Description);

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseAntiforgery();

app.MapStaticAssets();
app.MapHealthChecks("/health");
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
