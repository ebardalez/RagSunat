using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SunatRAG.Core.Configuration;
using SunatRAG.Core.Indexing;

namespace SunatRAG.Core.Retrieval;

/// <param name="Order">Indices into the candidate list, best first. Always a permutation of 0..n-1.</param>
/// <param name="FellBack">True when the model's answer was unusable and the original order was kept.</param>
public sealed record RerankResult(IReadOnlyList<int> Order, TimeSpan Elapsed, bool FellBack, string Raw);

/// <summary>
/// EXPERIMENT: listwise reranking with the chat model. Without Azure's semantic ranker (a purpose-built cross-encoder) the only
/// reranker available locally is the LLM itself, which is what makes this a measurement, not a feature:
/// the cost is one extra prompt of ~RerankCandidates × 200 tokens on a CPU/iGPU-bound qwen3:8b.
///
/// Listwise (one call, the model sees all candidates and returns an ordering) rather than pointwise (one call per candidate, N× the
/// latency). Depends on IChatClient only: the same class would run on Azure OpenAI unchanged.
///
/// qwen3 is a thinking model: left alone it spends the whole output budget on reasoning and returns an EMPTY answer (measured: 40-72 s
/// for nothing). The provider-neutral switch is ChatOptions.Reasoning (Effort = None); the provider maps it to its own flag.
/// </summary>
public sealed partial class LlmReranker(IChatClient chat, IOptions<RetrievalOptions> options, ILogger<LlmReranker> logger)
{
    private const string SystemPrompt =
        "Eres un evaluador de relevancia para consultas sobre normativa tributaria peruana (SUNAT). " +
        "Recibirás una pregunta y una lista numerada de fragmentos de normas. " +
        "Ordena los fragmentos del más útil al menos útil para responder la pregunta. " +
        "Responde únicamente con los números separados por comas, sin explicaciones.";

    public async Task<RerankResult> RerankAsync(string query, IReadOnlyList<ChunkRecord> candidates, CancellationToken ct)
    {
        var identity = Enumerable.Range(0, candidates.Count).ToList();
        if (candidates.Count < 2) return new RerankResult(identity, TimeSpan.Zero, false, "");

        var sw = Stopwatch.StartNew();
        List<ChatMessage> messages =
        [
            new(ChatRole.System, SystemPrompt),
            new(ChatRole.User, BuildUserPrompt(query, candidates, options.Value.RerankCharsPerChunk))
        ];

        // Deterministic and short: the answer is a list of at most ~10 small integers.
        var chatOptions = new ChatOptions
        {
            Temperature = 0f,
            MaxOutputTokens = 200,
            Reasoning = new ReasoningOptions { Effort = ReasoningEffort.None }
        };
        var response = await chat.GetResponseAsync(messages, chatOptions, ct);
        var raw = response.Text ?? "";

        var order = ParseOrder(raw, candidates.Count);
        if (order is null)
        {
            logger.LogWarning("El rerank con LLM devolvió una respuesta inutilizable; se conserva el orden original. Respuesta: {Raw}", raw);
            return new RerankResult(identity, sw.Elapsed, true, raw);
        }

        return new RerankResult(order, sw.Elapsed, false, raw);
    }

    internal static string BuildUserPrompt(string query, IReadOnlyList<ChunkRecord> candidates, int maxChars)
    {
        var sb = new StringBuilder();
        sb.Append("Pregunta: ").AppendLine(query).AppendLine().AppendLine("Fragmentos:");
        for (var i = 0; i < candidates.Count; i++)
        {
            var c = candidates[i];
            var text = c.Text.ReplaceLineEndings(" ").Trim();
            if (text.Length > maxChars) text = text[..maxChars] + "…";
            sb.Append('[').Append(i + 1).Append("] ").Append(c.Titulo);
            if (!string.IsNullOrWhiteSpace(c.Heading)) sb.Append(" — ").Append(c.Heading);
            sb.AppendLine().AppendLine(text).AppendLine();
        }

        sb.Append($"Ordena los {candidates.Count} fragmentos del más útil al menos útil. ");
        sb.Append("Responde solo con los números separados por comas (por ejemplo: 3, 1, 2).");
        return sb.ToString();
    }

    /// <summary>
    /// Model output -> a permutation of 0..count-1, or null if nothing usable. Tolerant on purpose: strips a reasoning block (its digits
    /// must not be read as ranks), ignores out-of-range and repeated numbers, and appends whatever the model forgot in original order,
    /// so a partial answer still yields a complete ordering.
    /// </summary>
    public static IReadOnlyList<int>? ParseOrder(string raw, int count)
    {
        var text = ThinkBlock().Replace(raw, "");
        if (text.Contains("<think>", StringComparison.OrdinalIgnoreCase)) return null; // reasoning never closed: no answer

        var order = new List<int>();
        foreach (Match m in Digits().Matches(text))
        {
            if (!int.TryParse(m.Value, out var n) || n < 1 || n > count) continue;
            if (!order.Contains(n - 1)) order.Add(n - 1);
        }

        if (order.Count == 0) return null;
        for (var i = 0; i < count; i++)
            if (!order.Contains(i)) order.Add(i);
        return order;
    }

    [GeneratedRegex(@"<think>.*?</think>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex ThinkBlock();

    [GeneratedRegex(@"\d+")]
    private static partial Regex Digits();
}
