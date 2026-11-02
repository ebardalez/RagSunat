using System.Text.Json;
using SunatRAG.Core.Documents;
using SunatRAG.Ingestion.Scraping;

namespace SunatRAG.Ingestion.Indexing;

/// <summary>Reads data/chunks.jsonl (written by the Phase 2 `extract` command).</summary>
public static class ChunkFile
{
    public static async Task<IReadOnlyList<DocumentChunk>> ReadAsync(string path, CancellationToken ct)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"No existe '{path}'. Ejecuta primero el comando `extract` (Fase 2).", path);

        var chunks = new List<DocumentChunk>();
        var lineNo = 0;
        await foreach (var line in File.ReadLinesAsync(path, ct))
        {
            lineNo++;
            if (string.IsNullOrWhiteSpace(line)) continue;
            var chunk = JsonSerializer.Deserialize<DocumentChunk>(line, ManifestStore.Json)
                        ?? throw new InvalidDataException($"{path}:{lineNo}: línea vacía o inválida.");
            chunks.Add(chunk);
        }

        return chunks;
    }
}
