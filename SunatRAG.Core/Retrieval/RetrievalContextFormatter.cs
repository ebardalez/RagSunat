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
    public static string Format(IReadOnlyList<RetrievedChunk> chunks)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < chunks.Count; i++)
        {
            sb.Append('[').Append(i + 1).Append("] ").AppendLine(chunks[i].Cita);
            sb.AppendLine(chunks[i].Record.Text.Trim());
            if (i < chunks.Count - 1) sb.AppendLine();
        }

        return sb.ToString();
    }
}
