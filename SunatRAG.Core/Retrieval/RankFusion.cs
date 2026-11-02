namespace SunatRAG.Core.Retrieval;

/// <param name="Score">The RRF score (only meaningful relative to other scores from the same query).</param>
/// <param name="VectorRank">1-based position in the dense ranking, null if the chunk was not in it.</param>
/// <param name="KeywordRank">1-based position in the BM25 ranking, null if the chunk was not in it.</param>
public sealed record FusedItem(string Key, double Score, int? VectorRank, int? KeywordRank);

/// <summary>
/// Reciprocal Rank Fusion.
///
/// Why not just add the scores? The two rankers speak different languages: cosine distance lives in [0, 2] (lower = better),
/// BM25 is unbounded and depends on corpus statistics (higher = better). There is no honest way to put 0.314 and 18.8 on one scale.
/// RRF throws the scores away and keeps only the ranks, which ARE comparable:
///
///     score(chunk) = sum over rankers of  weight / (k + rank)
///
/// A chunk that both rankers put near the top wins; a chunk that only one ranker likes still survives.
/// This is the same fusion Azure AI Search and Elasticsearch use for their "hybrid" modes; the SQLite connector has none,
/// so we own it (about 30 lines, and the best way to understand what hybrid search actually does).
/// </summary>
public static class RankFusion
{
    private sealed class Acc
    {
        public double Score;
        public int? Vector;
        public int? Keyword;
    }

    public static IReadOnlyList<FusedItem> ReciprocalRank(
        IReadOnlyList<string> vectorRanking,
        IReadOnlyList<string> keywordRanking,
        int k = 60,
        double vectorWeight = 1.0,
        double keywordWeight = 1.0)
    {
        var acc = new Dictionary<string, Acc>(StringComparer.Ordinal);

        void Add(IReadOnlyList<string> ranking, double weight, bool isVector)
        {
            for (var i = 0; i < ranking.Count; i++)
            {
                if (!acc.TryGetValue(ranking[i], out var a)) acc[ranking[i]] = a = new Acc();
                if ((isVector ? a.Vector : a.Keyword) is not null) continue; // duplicate within one list: first occurrence wins

                var rank = i + 1;
                if (isVector) a.Vector = rank; else a.Keyword = rank;
                a.Score += weight / (k + rank);
            }
        }

        Add(vectorRanking, vectorWeight, isVector: true);
        Add(keywordRanking, keywordWeight, isVector: false);

        // Deterministic order: score, then best individual rank, then key. Tests (and eval runs in Phase 6) need reproducibility.
        return acc
            .OrderByDescending(kv => kv.Value.Score)
            .ThenBy(kv => Math.Min(kv.Value.Vector ?? int.MaxValue, kv.Value.Keyword ?? int.MaxValue))
            .ThenBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => new FusedItem(kv.Key, kv.Value.Score, kv.Value.Vector, kv.Value.Keyword))
            .ToList();
    }
}
