using System.Net;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Retry;
using Polly.Timeout;

namespace SunatRAG.Ingestion.Scraping;

/// <summary>
/// Guarantees a minimum gap between the START of consecutive requests, whoever makes them.
/// One instance is shared by the robots.txt client and the main client, so the 1 req/sec rule is global.
/// </summary>
public sealed class RequestThrottle(TimeSpan minInterval, TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private long _lastStart;
    private bool _hasStarted;

    public async Task WaitAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_hasStarted)
            {
                var wait = minInterval - _time.GetElapsedTime(_lastStart);
                if (wait > TimeSpan.Zero) await Task.Delay(wait, _time, ct);
            }

            _lastStart = _time.GetTimestamp();
            _hasStarted = true;
        }
        finally
        {
            _gate.Release();
        }
    }
}

/// <summary>
/// The "good citizen" layer, as an HttpClient middleware: robots.txt check, then throttle, then send;
/// a 429 aborts the whole run instead of being retried.
/// </summary>
public sealed class PoliteHttpHandler(RequestThrottle throttle, RobotsPolicy robots) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var url = request.RequestUri ?? throw new InvalidOperationException("Request without URI.");

        if (!await robots.IsAllowedAsync(url, ct))
            throw new RobotsDisallowedException(url);

        await throttle.WaitAsync(ct);
        var response = await base.SendAsync(request, ct);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var retryAfter = response.Headers.RetryAfter?.Delta;
            response.Dispose();
            throw new RateLimitedException(url, retryAfter);
        }

        return response;
    }
}

/// <summary>
/// GET with a Polly v8 resilience pipeline: retry (exponential + jitter) on transient failures,
/// per-attempt timeout. Sits OUTSIDE <see cref="PoliteHttpHandler"/>, so every retry attempt is throttled again.
/// </summary>
public sealed class SunatHttpClient
{
    private static readonly UTF8Encoding Utf8 = new(false);

    private readonly HttpClient _http;
    private readonly ResiliencePipeline<HttpResponseMessage> _pipeline;

    public SunatHttpClient(HttpClient http, IOptions<ScrapingOptions> options, ILogger<SunatHttpClient> logger)
    {
        _http = http;
        var o = options.Value;

        _pipeline = new ResiliencePipelineBuilder<HttpResponseMessage>()
            .AddRetry(new RetryStrategyOptions<HttpResponseMessage>
            {
                MaxRetryAttempts = o.MaxRetries,
                Delay = o.RetryBaseDelay,
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                // RateLimitedException / RobotsDisallowedException are deliberately NOT handled: they must stop us.
                ShouldHandle = new PredicateBuilder<HttpResponseMessage>()
                    .Handle<HttpRequestException>()
                    .Handle<TimeoutRejectedException>()
                    .HandleResult(r => r.StatusCode >= HttpStatusCode.InternalServerError
                                       || r.StatusCode == HttpStatusCode.RequestTimeout),
                OnRetry = args =>
                {
                    logger.LogWarning(args.Outcome.Exception,
                        "Reintento {Attempt} (HTTP {Status}) en {Delay}.",
                        args.AttemptNumber + 1, (int?)args.Outcome.Result?.StatusCode, args.RetryDelay);
                    args.Outcome.Result?.Dispose();
                    return default;
                }
            })
            .AddTimeout(o.RequestTimeout) // inner: applies to each attempt (until headers arrive)
            .Build();
    }

    /// <summary>GET, returning as soon as headers arrive. The caller owns (and must dispose) the response.</summary>
    public async Task<HttpResponseMessage> GetAsync(Uri url, CancellationToken ct) =>
        await _pipeline.ExecuteAsync(
            async token => await _http.SendAsync(
                new HttpRequestMessage(HttpMethod.Get, url), HttpCompletionOption.ResponseHeadersRead, token),
            ct);

    public async Task<string> GetStringAsync(Uri url, CancellationToken ct)
    {
        using var response = await GetAsync(url, ct);
        response.EnsureSuccessStatusCode();
        // SUNAT sends "Content-Type: text/html" with no charset; the pages are UTF-8. Decode explicitly.
        var bytes = await response.Content.ReadAsByteArrayAsync(ct);
        return Utf8.GetString(bytes);
    }
}
