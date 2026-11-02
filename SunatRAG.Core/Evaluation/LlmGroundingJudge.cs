using System.Diagnostics;
using Microsoft.Extensions.AI;

namespace SunatRAG.Core.Evaluation;

public sealed record JudgeVerdict(bool? Supported, string Reason, TimeSpan Elapsed);

/// <summary>
/// EXPERIMENT: a model judges whether an answer is supported by the passages it cites ("LLM-as-judge").
///
/// Why it exists next to <see cref="GroundednessChecker"/>: the lexical check cannot see a reversed meaning. Why it is NOT in the request
/// path: it adds a full model call (seconds to tens of seconds on this hardware) and the judge is the same 8B model that may have
/// hallucinated, so its verdicts are evidence to compare against the deterministic check and the golden labels, never ground truth.
/// Depends only on IChatClient: the judge could run on a different (larger, remote) model by registering another client.
/// </summary>
public sealed class LlmGroundingJudge(IChatClient chat)
{
    private const string Instructions =
        """
        Eres un verificador estricto. Recibes una PREGUNTA, una RESPUESTA y los FRAGMENTOS que la respuesta cita.
        Decide si TODAS las afirmaciones de la respuesta están respaldadas por los fragmentos.
        Responde en la primera línea SOLO con SI o NO. En la segunda línea, una frase que explique el motivo.
        Responde NO si la respuesta afirma algo que los fragmentos no dicen, o dice lo contrario de lo que dicen.
        """;

    public async Task<JudgeVerdict> JudgeAsync(
        string question, string answer, IReadOnlyList<GroundingSource> citedSources, CancellationToken ct)
    {
        var passages = citedSources.Count == 0
            ? "(la respuesta no cita ningún fragmento)"
            : string.Join("\n\n", citedSources.Select(s => $"[{s.Number}] {s.Text}"));

        var prompt = $"PREGUNTA:\n{question}\n\nRESPUESTA:\n{answer}\n\nFRAGMENTOS CITADOS:\n{passages}";
        var options = new ChatOptions
        {
            Instructions = Instructions,
            Temperature = 0f,
            MaxOutputTokens = 200,
            Reasoning = new ReasoningOptions { Effort = ReasoningEffort.None }
        };

        var sw = Stopwatch.StartNew();
        var response = await chat.GetResponseAsync(prompt, options, ct);
        sw.Stop();

        var text = response.Text.Trim();
        var firstLine = text.Split('\n', 2)[0].Trim().TrimEnd('.').ToUpperInvariant();
        bool? supported = firstLine switch
        {
            "SI" or "SÍ" => true,
            "NO" => false,
            _ => null // the model did not follow the format: report "unknown" rather than guessing
        };

        return new JudgeVerdict(supported, text.Length > 240 ? text[..240] + "…" : text, sw.Elapsed);
    }
}
