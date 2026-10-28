using System.Net;
using System.Text;

namespace SunatRAG.Tests.Scraping;

/// <summary>Scripted transport: lets a test decide what "sunat.gob.pe" answers, and records what was asked.</summary>
internal sealed class StubHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    private readonly List<Uri> _requests = [];

    public IReadOnlyList<Uri> Requests => _requests;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        _requests.Add(request.RequestUri!);
        return Task.FromResult(respond(request));
    }
}

internal static class Responses
{
    public static HttpResponseMessage Html(string html) => new(HttpStatusCode.OK)
    {
        // Same as the real site: no charset in Content-Type.
        Content = new ByteArrayContent(Encoding.UTF8.GetBytes(html))
        {
            Headers = { ContentType = new("text/html") }
        }
    };

    public static HttpResponseMessage Pdf(string body) => new(HttpStatusCode.OK)
    {
        Content = new ByteArrayContent(Encoding.ASCII.GetBytes("%PDF-1.4 " + body))
        {
            Headers = { ContentType = new("application/pdf") }
        }
    };

    public static HttpResponseMessage Status(HttpStatusCode code) => new(code) { Content = new StringContent("") };
}

internal sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sunatrag-tests", Guid.NewGuid().ToString("N"));

    public TempDir() => Directory.CreateDirectory(Path);

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); }
        catch (IOException) { /* best effort: test scratch folder */ }
    }
}
