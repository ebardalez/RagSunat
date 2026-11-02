using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using SunatRAG.Core.Configuration;
using SunatRAG.Core.Documents;

namespace SunatRAG.Core.Indexing;

public static class SqliteConnectionStrings
{
    /// <summary>Same string for the vector collection and the FTS5 side: one database file, one place to look.</summary>
    public static string Build(IndexOptions o) => new SqliteConnectionStringBuilder
    {
        DataSource = Path.GetFullPath(o.DatabasePath),
        Mode = SqliteOpenMode.ReadWriteCreate,
        DefaultTimeout = 30
    }.ToString();
}

/// <param name="Score">Higher = better (negated FTS5 bm25).</param>
public sealed record KeywordHit(string ChunkId, string DocumentId, double Score);

/// <summary>
/// The BM25 half of hybrid search. The SQLite vector connector has no keyword/hybrid API (no HybridSearchAsync),
/// so the keyword index is an FTS5 table we manage ourselves, in the same .db file, keyed by the same ChunkId.
/// This is deliberately the one class that talks to SQLite directly (the vector side stays behind VectorStoreCollection).
///
/// Tokenizer: unicode61 with remove_diacritics 2 = case-folding + accent-insensitive ("resolución" matches "resolucion").
/// FTS5 ships no Spanish stemmer (porter is English-only), so "declaraciones" will not match "declaración";
/// that gap is what the dense vectors cover, and is the reason to fuse both rankings in Phase 4.
/// </summary>
public sealed partial class Fts5KeywordIndex(IOptions<IndexOptions> options)
{
    private const string Table = "chunk_fts";

    private static readonly HashSet<string> Stopwords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "al", "ante", "como", "con", "cual", "cuales", "de", "del", "desde", "donde", "e", "el", "ella", "en",
        "entre", "es", "esta", "este", "esto", "ha", "hay", "la", "las", "le", "les", "lo", "los", "me", "mi", "o",
        "para", "pero", "por", "que", "se", "segun", "ser", "si", "sin", "sobre", "su", "sus", "te", "tu", "u", "un",
        "una", "uno", "unos", "unas", "y", "ya"
    };

    private string ConnectionString => SqliteConnectionStrings.Build(options.Value);

    public async Task EnsureCreatedAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(options.Value.DatabasePath))!);
        await using var conn = await OpenAsync(ct);

        // WAL: the Web app can read while ingestion writes.
        await ExecuteAsync(conn, "PRAGMA journal_mode=WAL;", ct);
        await ExecuteAsync(conn,
            $"CREATE VIRTUAL TABLE IF NOT EXISTS {Table} USING fts5(" +
            "chunk_id UNINDEXED, document_id UNINDEXED, titulo, sumilla, heading, text, " +
            "tokenize = 'unicode61 remove_diacritics 2');", ct);
    }

    public async Task ResetAsync(CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await ExecuteAsync(conn, $"DROP TABLE IF EXISTS {Table};", ct);
        await EnsureCreatedAsync(ct);
    }

    /// <summary>Replace every FTS row of one document in a single transaction (idempotent; cheap, no model involved).</summary>
    public async Task ReplaceDocumentAsync(string documentId, IReadOnlyList<DocumentChunk> chunks, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);

        await using (var delete = conn.CreateCommand())
        {
            delete.Transaction = tx;
            delete.CommandText = $"DELETE FROM {Table} WHERE document_id = $doc;";
            delete.Parameters.AddWithValue("$doc", documentId);
            await delete.ExecuteNonQueryAsync(ct);
        }

        await using (var insert = conn.CreateCommand())
        {
            insert.Transaction = tx;
            insert.CommandText =
                $"INSERT INTO {Table}(chunk_id, document_id, titulo, sumilla, heading, text) " +
                "VALUES ($id, $doc, $titulo, $sumilla, $heading, $text);";
            var id = insert.Parameters.Add("$id", SqliteType.Text);
            var doc = insert.Parameters.Add("$doc", SqliteType.Text);
            var titulo = insert.Parameters.Add("$titulo", SqliteType.Text);
            var sumilla = insert.Parameters.Add("$sumilla", SqliteType.Text);
            var heading = insert.Parameters.Add("$heading", SqliteType.Text);
            var text = insert.Parameters.Add("$text", SqliteType.Text);

            foreach (var c in chunks)
            {
                ct.ThrowIfCancellationRequested();
                id.Value = c.ChunkId;
                doc.Value = c.DocumentId;
                titulo.Value = c.Titulo;
                sumilla.Value = (object?)c.Sumilla ?? "";
                heading.Value = (object?)c.Heading ?? "";
                text.Value = c.Text;
                await insert.ExecuteNonQueryAsync(ct);
            }
        }

        await tx.CommitAsync(ct);
    }

    public async Task DeleteDocumentAsync(string documentId, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"DELETE FROM {Table} WHERE document_id = $doc;";
        cmd.Parameters.AddWithValue("$doc", documentId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<string>> DocumentIdsAsync(CancellationToken ct)
    {
        var ids = new List<string>();
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT DISTINCT document_id FROM {Table} ORDER BY document_id;";
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) ids.Add(reader.GetString(0));
        return ids;
    }

    public async Task<long> CountAsync(CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT count(*) FROM {Table};";
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
    }

    /// <summary>BM25 over titulo/sumilla/heading/text. Column weights favour the structural columns.</summary>
    public async IAsyncEnumerable<KeywordHit> SearchAsync(
        string query, int top, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var match = BuildMatchQuery(query);
        if (match is null) yield break;

        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        // bm25() takes one weight per column, in table order (the two UNINDEXED id columns count: weight 0).
        cmd.CommandText =
            $"SELECT chunk_id, document_id, bm25({Table}, 0, 0, 2.0, 1.5, 2.0, 1.0) AS score " +
            $"FROM {Table} WHERE {Table} MATCH $q ORDER BY score LIMIT $top;";
        cmd.Parameters.AddWithValue("$q", match);
        cmd.Parameters.AddWithValue("$top", top);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            yield return new KeywordHit(reader.GetString(0), reader.GetString(1), -reader.GetDouble(2));
    }

    /// <summary>
    /// Free text -> a safe FTS5 MATCH expression: tokens quoted (so user text can never inject FTS operators such as
    /// NEAR or column filters) and joined with OR; BM25 then ranks chunks that match more, and rarer, terms higher.
    /// Returns null when nothing searchable remains.
    /// </summary>
    public static string? BuildMatchQuery(string query)
    {
        var tokens = new List<string>();
        foreach (Match m in Token().Matches(query))
        {
            var t = m.Value.ToLowerInvariant();
            var isNumber = t.All(char.IsDigit);
            if (!isNumber && (t.Length < 2 || Stopwords.Contains(t))) continue;
            if (!tokens.Contains(t)) tokens.Add(t);
            if (tokens.Count == 32) break;
        }

        return tokens.Count == 0 ? null : string.Join(" OR ", tokens.Select(t => $"\"{t}\""));
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct);
        return conn;
    }

    private static async Task ExecuteAsync(SqliteConnection conn, string sql, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    [GeneratedRegex(@"[\p{L}\p{N}]+")]
    private static partial Regex Token();
}
