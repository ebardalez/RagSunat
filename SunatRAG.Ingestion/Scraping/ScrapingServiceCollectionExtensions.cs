using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace SunatRAG.Ingestion.Scraping;

public static class ScrapingServiceCollectionExtensions
{
    private const string RobotsClientName = "sunat-robots";

    public static IServiceCollection AddSunatScraping(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<ScrapingOptions>()
            .Bind(configuration.GetSection(ScrapingOptions.SectionName))
            .Validate(o => Uri.TryCreate(o.BaseUrl, UriKind.Absolute, out _), "Scraping:BaseUrl debe ser una URL absoluta.")
            .Validate(o => o.MinRequestInterval >= ScrapingOptions.MinimumInterval,
                "Scraping:MinRequestInterval no puede ser menor a 1 segundo (regla de cortesía del proyecto).")
            .Validate(o => o.MaxRetries is >= 0 and <= 10, "Scraping:MaxRetries debe estar entre 0 y 10.")
            .Validate(o => o.FromYear >= 1996, "Scraping:FromYear debe ser >= 1996.")
            .Validate(o => !string.IsNullOrWhiteSpace(o.UserAgent), "Scraping:UserAgent es obligatorio.");

        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(sp => new DataPaths(Path.GetFullPath(sp.GetRequiredService<IOptions<ScrapingOptions>>().Value.DataDirectory)));
        services.AddSingleton(sp => new RequestThrottle(sp.GetRequiredService<IOptions<ScrapingOptions>>().Value.MinRequestInterval));

        // robots.txt travels on its own client WITHOUT the polite handler (otherwise: infinite recursion),
        // but through the same throttle instance.
        ConfigureClient(services.AddHttpClient(RobotsClientName));
        services.AddSingleton(sp =>
        {
            var o = sp.GetRequiredService<IOptions<ScrapingOptions>>().Value;
            return new RobotsPolicy(
                sp.GetRequiredService<IHttpClientFactory>().CreateClient(RobotsClientName),
                sp.GetRequiredService<RequestThrottle>(),
                o.ProductToken,
                sp.GetRequiredService<ILogger<RobotsPolicy>>());
        });

        services.AddTransient<PoliteHttpHandler>();
        ConfigureClient(services.AddHttpClient<SunatHttpClient>()).AddHttpMessageHandler<PoliteHttpHandler>();

        services.AddSingleton<PdfStore>();
        services.AddSingleton<PdfDownloader>();
        services.AddSingleton<ManifestStore>();
        services.AddSingleton<IndexPageCache>();
        services.AddSingleton<ScrapeRunner>();
        return services;
    }

    private static IHttpClientBuilder ConfigureClient(IHttpClientBuilder builder) => builder
        .ConfigureHttpClient((sp, client) =>
        {
            var o = sp.GetRequiredService<IOptions<ScrapingOptions>>().Value;
            client.BaseAddress = new Uri(o.BaseUrl);
            client.DefaultRequestHeaders.UserAgent.ParseAdd(o.BuildUserAgent());
            client.DefaultRequestHeaders.AcceptLanguage.Add(new StringWithQualityHeaderValue("es-PE"));
            client.DefaultRequestHeaders.AcceptLanguage.Add(new StringWithQualityHeaderValue("es", 0.9));
            client.Timeout = Timeout.InfiniteTimeSpan; // the Polly pipeline owns timeouts (per attempt)
        })
        .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        });
}
