namespace SunatRAG.Core.Retrieval;

/// <summary>
/// The seam the agent layer depends on. <see cref="HybridRetriever"/> is sealed and wired to SQLite + an embedder; the agent only needs
/// "question in, ranked chunks out", so it takes this interface and tests substitute it (NSubstitute) without a database or Ollama.
/// </summary>
public interface IChunkRetriever
{
    Task<RetrievalResult> RetrieveAsync(RetrievalRequest request, CancellationToken ct = default);
}
