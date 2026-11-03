using System.Net;
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

    /// <summary>
    /// Conditional GET with the validators saved in the manifest. Returns null when the server says 304 (the PDF we have is current).
    /// A 200 returns the (possibly identical) bytes: servers that ignore validators always answer 200, so the CALLER compares hashes.
    /// </summary>
    public async Task<DownloadResult?> RevalidateAsync(Uri url, CacheValidators validators, CancellationToken ct)
    {
        using var response = await http.GetAsync(url, ct, validators);
        if (response.StatusCode == HttpStatusCode.NotModified) return null;

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

/// <summary>
/// Caches index HTML on disk so re-runs, and parser debugging, do not hit the site again. Two layers:
///   1. TTL: inside <see cref="ScrapingOptions.IndexCacheTtl"/> the file is used with NO request at all.
///   2. Revalidation: past the TTL (or with refresh) we send a conditional GET with the ETag / Last-Modified saved next to the file
///      (<c>{id}.html.meta.json</c>). A 304 means "unchanged": the file's timestamp is renewed and nothing is downloaded or rewritten.
///      Servers that send no validators simply get a normal GET, so this never makes things worse.
/// </summary>
public sealed class IndexPageCache(
    DataPaths paths,
    SunatHttpClient http,
    IOptions<ScrapingOptions> options,
    TimeProvider time,
    ILogger<IndexPageCache> logger)
{
    private static readonly JsonSerializerOptions MetaJson = new(JsonSerializerDefaults.Web);

    public async Task<string> GetAsync(Uri url, bool refresh, CancellationToken ct)
    {
        var file = Path.Combine(paths.IndexCache, NormaIds.FromUrl(url) + ".html");
        var cached = File.Exists(file);

        if (!refresh && cached && time.GetUtcNow() - File.GetLastWriteTimeUtc(file) < options.Value.IndexCacheTtl)
        {
            logger.LogDebug("Índice en caché: {Url}", url);
            return await File.ReadAllTextAsync(file, Encoding.UTF8, ct);
        }

        // Validators are only meaningful together with the file they describe.
        var validators = cached ? await ReadValidatorsAsync(file + ".meta.json", ct) : null;
        var result = await http.GetStringIfChangedAsync(url, validators, ct);

        if (result.Body is null)
        {
            // 304: same bytes. Renew the timestamp so the TTL window restarts without rewriting the file.
            File.SetLastWriteTimeUtc(file, time.GetUtcNow().UtcDateTime);
            logger.LogInformation("Índice sin cambios (304): {Url}", url);
            return await File.ReadAllTextAsync(file, Encoding.UTF8, ct);
        }

        Directory.CreateDirectory(paths.IndexCache);
        await File.WriteAllTextAsync(file, result.Body, new UTF8Encoding(false), ct);
        await File.WriteAllTextAsync(file + ".meta.json", JsonSerializer.Serialize(result.Validators, MetaJson), new UTF8Encoding(false), ct);
        return result.Body;
    }

    private static async Task<CacheValidators?> ReadValidatorsAsync(string metaFile, CancellationToken ct)
    {
        if (!File.Exists(metaFile)) return null;
        try
        {
            return JsonSerializer.Deserialize<CacheValidators>(await File.ReadAllTextAsync(metaFile, ct), MetaJson);
        }
        catch (JsonException)
        {
            return null; // unreadable sidecar: fall back to an unconditional GET
        }
    }
}
