using SunatRAG.Core.Evaluation;

namespace SunatRAG.Core.Agents;

/// <summary>
/// What the APP concluded about an answer after the model finished writing it: resolved citations, invented citation numbers,
/// numbers that no source contains, uncited claims. The model's own claim of being grounded is never trusted; this is computed from
/// the sources the conversation actually showed it.
/// </summary>
public sealed record AnswerAudit(CitationResolution Citations, GroundingReport Grounding, bool HasDisclaimer, bool LooksLikeRefusal)
{
    /// <summary>
    /// Worth showing a warning in the UI. Deliberately limited to the two high-precision signals (invented citation numbers and
    /// numbers absent from every source); the fuzzier ones (weak lexical support, uncited sentences) are reported by `eval agent`.
    /// </summary>
    public bool NeedsAttention => Citations.Invalid.Count > 0 || Grounding.UngroundedNumbers.Count > 0;
}

/// <summary>Raised once per answer, after the last <see cref="TextDelta"/>, only when the stream completed.</summary>
public sealed record AuditReported(AnswerAudit Audit) : ChatEvent;

public static class AnswerAuditor
{
    public static AnswerAudit Audit(string question, string answer, CitationRegistry registry)
    {
        var citations = registry.Resolve(answer);
        var sources = registry.Snapshot();
        // The question and "2025" (the corpus year, stated in the instructions) may legitimately be echoed by the answer.
        var grounding = GroundednessChecker.Check(answer, sources, question + " 2025");
        return new AnswerAudit(citations, grounding, GroundednessChecker.HasDisclaimer(answer), AnswerChecks.LooksLikeRefusal(answer));
    }
}
