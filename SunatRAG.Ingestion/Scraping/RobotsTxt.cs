using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace SunatRAG.Ingestion.Scraping;

/// <summary>
/// Minimal RFC 9309 robots.txt evaluation: user-agent groups, Allow/Disallow, longest-match wins
/// (Allow wins ties), "*" and "$" wildcards. Crawl-delay is ignored; our own floor is already 1 req/sec.
/// </summary>
public sealed class RobotsTxt
{
    private sealed class Rule(bool allow, string pattern)
    {
        private readonly Regex _regex = new(ToRegex(pattern), RegexOptions.CultureInvariant);

        public bool Allow { get; } = allow;
        public int Specificity { get; } = pattern.Length;
        public bool Matches(string pathAndQuery) => _regex.IsMatch(pathAndQuery);

        private static string ToRegex(string pattern)
        {
            var anchored = pattern.EndsWith('$');
            if (anchored) pattern = pattern[..^1];
            var body = Regex.Escape(pattern).Replace("\\*", ".*");
            return "^" + body + (anchored ? "$" : "");
        }
    }

    private readonly IReadOnlyList<Rule> _rules;

    private RobotsTxt(IReadOnlyList<Rule> rules) => _rules = rules;

    public static RobotsTxt AllowAll { get; } = new([]);
    public static RobotsTxt DisallowAll { get; } = new([new Rule(false, "/")]);

    public static RobotsTxt Parse(string content, string productToken)
    {
        var groups = new List<(List<string> Agents, List<Rule> Rules)>();
        List<string>? agents = null;
        List<Rule>? rules = null;
        var collectingRules = false;

        foreach (var rawLine in content.Split('\n'))
        {
            var line = rawLine;
            var hash = line.IndexOf('#');
            if (hash >= 0) line = line[..hash];
            line = line.Trim();
            var colon = line.IndexOf(':');
            if (colon < 0) continue;

            var field = line[..colon].Trim().ToLowerInvariant();
            var value = line[(colon + 1)..].Trim();

            if (field == "user-agent")
            {
                // A user-agent line after rules starts a new group; consecutive ones share the group.
                if (agents is null || rules is null || collectingRules)
                {
                    agents = [];
                    rules = [];
                    groups.Add((agents, rules));
                    collectingRules = false;
                }

                agents.Add(value.ToLowerInvariant());
            }
            else if (field is "allow" or "disallow" && rules is not null)
            {
                collectingRules = true;
                if (value.Length == 0) continue; // empty Disallow = nothing disallowed
                rules.Add(new Rule(field == "allow", value));
            }
        }

        var token = productToken.ToLowerInvariant();
        var matching = groups.Where(g => g.Agents.Contains(token)).ToList();
        if (matching.Count == 0) matching = groups.Where(g => g.Agents.Contains("*")).ToList();

        return new RobotsTxt([.. matching.SelectMany(g => g.Rules)]);
    }

    public bool IsAllowed(string pathAndQuery)
    {
        Rule? best = null;
        foreach (var rule in _rules)
        {
            if (!rule.Matches(pathAndQuery)) continue;
            if (best is null
                || rule.Specificity > best.Specificity
                || (rule.Specificity == best.Specificity && rule.Allow && !best.Allow))
            {
                best = rule;
            }
        }

        return best?.Allow ?? true;
    }
}

/// <summary>Fetches robots.txt once per origin and answers "may I GET this URL?".</summary>
public sealed class RobotsPolicy(
    HttpClient http,
    RequestThrottle throttle,
    string productToken,
    ILogger<RobotsPolicy> logger)
{
    private readonly Dictionary<string, RobotsTxt> _byOrigin = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _lock = new(1, 1);

    public async Task<bool> IsAllowedAsync(Uri url, CancellationToken ct)
    {
        if (url.AbsolutePath.Equals("/robots.txt", StringComparison.OrdinalIgnoreCase)) return true;
        var robots = await GetAsync(url, ct);
        return robots.IsAllowed(url.PathAndQuery);
    }

    private async Task<RobotsTxt> GetAsync(Uri url, CancellationToken ct)
    {
        var origin = url.GetLeftPart(UriPartial.Authority);
        await _lock.WaitAsync(ct);
        try
        {
            if (_byOrigin.TryGetValue(origin, out var cached)) return cached;
            var robots = await FetchAsync(new Uri(origin + "/robots.txt"), ct);
            _byOrigin[origin] = robots;
            return robots;
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<RobotsTxt> FetchAsync(Uri robotsUrl, CancellationToken ct)
    {
        await throttle.WaitAsync(ct);
        using var response = await http.GetAsync(robotsUrl, ct);

        if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            throw new RateLimitedException(robotsUrl, response.Headers.RetryAfter?.Delta);

        var status = (int)response.StatusCode;
        if (response.IsSuccessStatusCode)
        {
            var text = await response.Content.ReadAsStringAsync(ct);
            logger.LogInformation("robots.txt leído de {Url}.", robotsUrl);
            return RobotsTxt.Parse(text, productToken);
        }

        if (status is >= 400 and < 500)
        {
            // RFC 9309 §2.3.1.3: a 4xx means "no robots.txt", i.e. no restrictions.
            logger.LogInformation("Sin robots.txt en {Url} (HTTP {Status}): sin restricciones declaradas.", robotsUrl, status);
            return RobotsTxt.AllowAll;
        }

        // RFC 9309 §2.3.1.4: server errors -> assume everything is disallowed. Conservative on purpose.
        logger.LogWarning("robots.txt respondió HTTP {Status}; se asume Disallow: / durante esta ejecución.", status);
        return RobotsTxt.DisallowAll;
    }
}
