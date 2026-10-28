using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SunatRAG.Ingestion.Scraping;

namespace SunatRAG.Tests.Scraping;

public class RobotsTxtTests
{
    private const string Token = "SunatRAG";

    [Fact]
    public void Empty_file_allows_everything() =>
        Assert.True(RobotsTxt.Parse("", Token).IsAllowed("/anything"));

    [Fact]
    public void Wildcard_group_applies_when_no_specific_group_exists()
    {
        var robots = RobotsTxt.Parse("User-agent: *\nDisallow: /private/\n", Token);

        Assert.False(robots.IsAllowed("/private/x.pdf"));
        Assert.True(robots.IsAllowed("/public/x.pdf"));
    }

    [Fact]
    public void Specific_group_wins_over_wildcard()
    {
        var content = "User-agent: *\nDisallow: /\n\nUser-agent: sunatrag\nDisallow:\n";

        Assert.True(RobotsTxt.Parse(content, Token).IsAllowed("/legislacion/x.pdf"));
        Assert.False(RobotsTxt.Parse(content, "OtherBot").IsAllowed("/legislacion/x.pdf"));
    }

    [Fact]
    public void Longest_match_wins_and_allow_wins_ties()
    {
        var robots = RobotsTxt.Parse("User-agent: *\nDisallow: /a/\nAllow: /a/b\n", Token);

        Assert.True(robots.IsAllowed("/a/b/c"));
        Assert.False(robots.IsAllowed("/a/x"));

        var tie = RobotsTxt.Parse("User-agent: *\nDisallow: /same\nAllow: /same\n", Token);
        Assert.True(tie.IsAllowed("/same"));
    }

    [Fact]
    public void Star_and_dollar_wildcards_are_supported()
    {
        var robots = RobotsTxt.Parse("User-agent: *\nDisallow: /*.pdf$\n", Token);

        Assert.False(robots.IsAllowed("/x/y.pdf"));
        Assert.True(robots.IsAllowed("/x/y.pdf?download=1"));
        Assert.True(robots.IsAllowed("/x/y.html"));
    }

    [Fact]
    public void Comments_and_crlf_line_endings_are_tolerated()
    {
        var robots = RobotsTxt.Parse("# hola\r\nUser-agent: * # todos\r\nDisallow: /x # no\r\n", Token);

        Assert.False(robots.IsAllowed("/x/1"));
        Assert.True(robots.IsAllowed("/y"));
    }

    [Fact]
    public void Static_all_or_nothing_policies()
    {
        Assert.True(RobotsTxt.AllowAll.IsAllowed("/x"));
        Assert.False(RobotsTxt.DisallowAll.IsAllowed("/x"));
    }
}

public class RequestThrottleTests
{
    [Fact]
    public async Task Consecutive_requests_start_at_least_one_interval_apart()
    {
        var throttle = new RequestThrottle(TimeSpan.FromMilliseconds(100));
        var sw = Stopwatch.StartNew();

        await throttle.WaitAsync(CancellationToken.None); // immediate
        await throttle.WaitAsync(CancellationToken.None); // +100 ms
        await throttle.WaitAsync(CancellationToken.None); // +200 ms

        Assert.True(sw.ElapsedMilliseconds >= 180, $"elapsed {sw.ElapsedMilliseconds} ms");
    }

    [Fact]
    public async Task Waiting_honours_cancellation()
    {
        var throttle = new RequestThrottle(TimeSpan.FromSeconds(30));
        await throttle.WaitAsync(CancellationToken.None);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => throttle.WaitAsync(cts.Token));
    }
}

public class PoliteHttpHandlerTests
{
    private static readonly Uri Origin = new("https://sunat.test/");

    private static (HttpClient Client, StubHttpHandler Site, StubHttpHandler RobotsServer) Create(
        Func<HttpRequestMessage, HttpResponseMessage> site,
        Func<HttpRequestMessage, HttpResponseMessage>? robots = null)
    {
        var throttle = new RequestThrottle(TimeSpan.Zero);
        var robotsServer = new StubHttpHandler(robots ?? (_ => Responses.Status(HttpStatusCode.NotFound)));
        var policy = new RobotsPolicy(
            new HttpClient(robotsServer) { BaseAddress = Origin }, throttle, "SunatRAG", NullLogger<RobotsPolicy>.Instance);

        var siteStub = new StubHttpHandler(site);
        var handler = new PoliteHttpHandler(throttle, policy) { InnerHandler = siteStub };
        return (new HttpClient(handler) { BaseAddress = Origin }, siteStub, robotsServer);
    }

    [Fact]
    public async Task Missing_robots_txt_means_allowed_and_robots_is_fetched_only_once()
    {
        var (client, site, robots) = Create(_ => Responses.Html("ok"));

        await client.GetAsync("/legislacion/a.html");
        await client.GetAsync("/legislacion/b.html");

        Assert.Equal(2, site.Requests.Count);
        Assert.Single(robots.Requests);
        Assert.Equal("/robots.txt", robots.Requests[0].AbsolutePath);
    }

    [Fact]
    public async Task Disallowed_url_is_never_requested()
    {
        var (client, site, _) = Create(
            _ => Responses.Html("ok"),
            _ => Responses.Html("User-agent: *\nDisallow: /legislacion/privado/\n"));

        await Assert.ThrowsAsync<RobotsDisallowedException>(() => client.GetAsync("/legislacion/privado/x.pdf"));

        Assert.Empty(site.Requests);
    }

    [Fact]
    public async Task Http_429_aborts_with_retry_after()
    {
        var (client, _, _) = Create(_ =>
        {
            var response = Responses.Status(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(120));
            return response;
        });

        var ex = await Assert.ThrowsAsync<RateLimitedException>(() => client.GetAsync("/legislacion/a.html"));

        Assert.Equal(TimeSpan.FromSeconds(120), ex.RetryAfter);
    }
}

public class SunatHttpClientTests
{
    private static IOptions<ScrapingOptions> Fast(int retries = 3) =>
        Options.Create(new ScrapingOptions { MaxRetries = retries, RetryBaseDelay = TimeSpan.FromMilliseconds(1) });

    [Fact]
    public async Task Transient_5xx_is_retried_until_it_succeeds()
    {
        var calls = 0;
        var stub = new StubHttpHandler(_ => ++calls < 3 ? Responses.Status(HttpStatusCode.ServiceUnavailable) : Responses.Html("ok"));
        var client = new SunatHttpClient(
            new HttpClient(stub) { BaseAddress = new Uri("https://sunat.test/") }, Fast(), NullLogger<SunatHttpClient>.Instance);

        var text = await client.GetStringAsync(new Uri("https://sunat.test/x"), CancellationToken.None);

        Assert.Equal("ok", text);
        Assert.Equal(3, stub.Requests.Count);
    }

    [Fact]
    public async Task Persistent_5xx_gives_up_after_max_retries_and_surfaces_the_error()
    {
        var stub = new StubHttpHandler(_ => Responses.Status(HttpStatusCode.BadGateway));
        var client = new SunatHttpClient(
            new HttpClient(stub), Fast(retries: 2), NullLogger<SunatHttpClient>.Instance);

        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetStringAsync(new Uri("https://sunat.test/x"), CancellationToken.None));

        Assert.Equal(3, stub.Requests.Count); // 1 attempt + 2 retries
    }

    [Fact]
    public async Task Http_429_is_not_retried()
    {
        var throttle = new RequestThrottle(TimeSpan.Zero);
        var robots = new RobotsPolicy(
            new HttpClient(new StubHttpHandler(_ => Responses.Status(HttpStatusCode.NotFound))) { BaseAddress = new Uri("https://sunat.test/") },
            throttle, "SunatRAG", NullLogger<RobotsPolicy>.Instance);
        var site = new StubHttpHandler(_ => Responses.Status(HttpStatusCode.TooManyRequests));
        var http = new HttpClient(new PoliteHttpHandler(throttle, robots) { InnerHandler = site });
        var client = new SunatHttpClient(http, Fast(), NullLogger<SunatHttpClient>.Instance);

        await Assert.ThrowsAsync<RateLimitedException>(() => client.GetStringAsync(new Uri("https://sunat.test/x"), CancellationToken.None));

        Assert.Single(site.Requests);
    }

    [Fact]
    public async Task Html_is_decoded_as_utf8_even_without_a_charset_header()
    {
        var stub = new StubHttpHandler(_ => Responses.Html("Resolución N° 1 — año"));
        var client = new SunatHttpClient(new HttpClient(stub), Fast(), NullLogger<SunatHttpClient>.Instance);

        var text = await client.GetStringAsync(new Uri("https://sunat.test/x"), CancellationToken.None);

        Assert.Equal("Resolución N° 1 — año", text);
    }
}

public class ScrapingOptionsTests
{
    [Fact]
    public void User_agent_includes_contact_when_provided()
    {
        var o = new ScrapingOptions { Contact = "ramon@example.com" };

        Assert.EndsWith("contact: ramon@example.com", o.BuildUserAgent());
        Assert.Equal("SunatRAG", o.ProductToken);
    }
}
