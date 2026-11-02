using System.Text;

namespace SunatRAG.Core.Retrieval;

/// <summary>
/// Renders retrieved chunks as the numbered "sources" block of the prompt:
///
///   [1] Resolución de Superintendencia N° 000386-2025, págs. 1-2 (CONSIDERANDO)
///   Que el artículo 4 de la Ley N.° 31940 ...
///
/// The model is told to cite as "[1]". The app, not the model, maps the number back to RetrievedChunk (document, pages, URL):
/// citations stay correct even if the model is sloppy, and no URL has to be spent as prompt tokens.
/// </summary>
public static class RetrievalContextFormatter
{
    /// <summary>Numbers chunks 1..n by position (single-shot retrieval, e.g. the CLI).</summary>
    public static string Format(IReadOnlyList<RetrievedChunk> chunks) =>
        Format(chunks.Select((c, i) => (i + 1, c)));

    /// <summary>
    /// Uses caller-assigned numbers. The agent needs this: several searches can happen in one conversation, and "[1]" must keep meaning
    /// the same chunk in every tool result and every answer (see CitationRegistry).
    /// </summary>
    public static string Format(IEnumerable<(int Number, RetrievedChunk Chunk)> numbered)
    {
        var sb = new StringBuilder();
        foreach (var (number, chunk) in numbered)
        {
            if (sb.Length > 0) sb.AppendLine();
            sb.Append('[').Append(number).Append("] ").AppendLine(chunk.Cita);
            sb.AppendLine(chunk.Record.Text.Trim());
        }

        return sb.ToString().TrimEnd();
    }
}
