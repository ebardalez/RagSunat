using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SunatRAG.Core.Documents;

namespace SunatRAG.Ingestion.Scraping;

public sealed record StoredPdf(string Sha256, string RelativePath, long SizeBytes, bool IsDuplicate);

/// <summary>
/// Content-addressed PDF storage: pdfs/ab/ab12....pdf. Two URLs serving identical bytes share one file,
/// which is the "dedupe by hash" requirement.
/// </summary>
public sealed class PdfStore(DataPaths paths)
{
    public async Task<StoredPdf> SaveAsync(Stream content, CancellationToken ct)
    {
        Directory.CreateDirectory(paths.Temp);
        var temp = Path.Combine(paths.Temp, $"{Guid.NewGuid():N}.part");

        try
        {
            await using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                await content.CopyToAsync(file, ct);
            }

            string hash;
            long size;
            await using (var read = new FileStream(temp, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true))
            {
                // Some servers answer 200 + an HTML error page. Check the magic number before trusting the bytes.
                var head = new byte[5];
                var n = await read.ReadAtLeastAsync(head, head.Length, throwOnEndOfStream: false, ct);
                if (n < head.Length || !head.AsSpan().SequenceEqual("%PDF-"u8))
                    throw new InvalidDataException("El contenido descargado no es un PDF (falta la cabecera %PDF-).");

                read.Position = 0;
                hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(read, ct));
                size = read.Length;
            }

            var relative = $"pdfs/{hash[..2]}/{hash}.pdf";
            var full = Path.Combine(paths.Root, relative);
            if (File.Exists(full)) return new StoredPdf(hash, relative, size, IsDuplicate: true);

            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.Move(temp, full);
            return new StoredPdf(hash, relative, size, IsDuplicate: false);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp); // our own scratch file
        }
    }
}

public sealed record DownloadResult(StoredPdf Stored, string? ETag, DateTimeOffset? LastModified);

public sealed class PdfDownloader(SunatHttpClient http, PdfStore store)
{
    public async Task<DownloadResult> DownloadAsync(Uri url, CancellationToken ct)
    {
        using var response = await http.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var stored = await store.SaveAsync(stream, ct);
        return new DownloadResult(stored, response.Headers.ETag?.Tag, response.Content.Headers.LastModified);
    }
}

/// <summary>
/// Append-only JSON Lines log of downloaded documents. Appending one line per document AFTER the file is on disk
/// is what makes a crashed run resumable; the last line for an Id wins.
/// </summary>
public sealed class ManifestStore(DataPaths paths, ILogger<ManifestStore> logger)
{
    public static readonly JsonSerializerOptions Json = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    public async Task<IReadOnlyDictionary<string, NormaDocument>> LoadAsync(CancellationToken ct)
    {
        var result = new Dictionary<string, NormaDocument>(StringComparer.Ordinal);
        if (!File.Exists(paths.Manifest)) return result;

        var lineNo = 0;
        await foreach (var line in File.ReadLinesAsync(paths.Manifest, ct))
        {
            lineNo++;
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var doc = JsonSerializer.Deserialize<NormaDocument>(line, Json);
                if (doc is not null) result[doc.Id] = doc;
            }
            catch (JsonException ex)
            {
                // Typically a half-written last line after a crash.
                logger.LogWarning(ex, "Línea {Line} del manifest ilegible; se ignora.", lineNo);
            }
        }

        return result;
    }

    public async Task AppendAsync(NormaDocument doc, CancellationToken ct)
    {
        Directory.CreateDirectory(paths.Root);
        var prefix = EndsWithoutNewline(paths.Manifest) ? "\n" : "";
        var line = prefix + JsonSerializer.Serialize(doc, Json) + "\n";
        await File.AppendAllTextAsync(paths.Manifest, line, new UTF8Encoding(false), ct);
    }

    private static bool EndsWithoutNewline(string path)
    {
        if (!File.Exists(path)) return false;
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (fs.Length == 0) return false;
        fs.Seek(-1, SeekOrigin.End);
        return fs.ReadByte() != '\n';
    }
}

/// <summary>Caches index HTML on disk (TTL) so re-runs, and parser debugging, do not hit the site again.</summary>
public sealed class IndexPageCache(
    DataPaths paths,
    SunatHttpClient http,
    IOptions<ScrapingOptions> options,
    TimeProvider time,
    ILogger<IndexPageCache> logger)
{
    public async Task<string> GetAsync(Uri url, bool refresh, CancellationToken ct)
    {
        var file = Path.Combine(paths.IndexCache, NormaIds.FromUrl(url) + ".html");

        if (!refresh && File.Exists(file)
            && time.GetUtcNow() - File.GetLastWriteTimeUtc(file) < options.Value.IndexCacheTtl)
        {
            logger.LogDebug("Índice en caché: {Url}", url);
            return await File.ReadAllTextAsync(file, Encoding.UTF8, ct);
        }

        var html = await http.GetStringAsync(url, ct);
        Directory.CreateDirectory(paths.IndexCache);
        await File.WriteAllTextAsync(file, html, new UTF8Encoding(false), ct);
        return html;
    }
}
