using SunatRAG.Core.Evaluation;

namespace SunatRAG.Core.Agents;

/// <summary>
/// What the APP concluded about an answer after the model finished writing it: resolved citations, invented citation numbers,
/// numbers that no source contains, uncited claims. The model's own claim of being grounded is never trusted; this is computed from
/// the sources the conversation actually showed it.
/// </summary>
public sealed record AnswerAudit(
    CitationResolution Citations, GroundingReport Grounding, bool HasDisclaimer, bool LooksLikeRefusal, bool LeaksInstructions = false)
{
    /// <summary>
    /// Worth showing a warning in the UI. Deliberately limited to the high-precision signals (invented citation numbers, numbers absent
    /// from every source, a reproduced system prompt); the fuzzier ones (weak lexical support, uncited sentences) are reported by `eval agent`.
    /// </summary>
    public bool NeedsAttention => Citations.Invalid.Count > 0 || Grounding.UngroundedNumbers.Count > 0 || LeaksInstructions;
}

/// <summary>
/// The output guard fired: the text the user sees must be REPLACED by <see cref="Text"/>. Emitted after the last <see cref="TextDelta"/>
/// and before <see cref="AuditReported"/>. Streaming means the leaked text was already on screen; a guard that never lets it appear would
/// have to buffer the whole answer, which costs the streaming UX on a model that already takes a minute. Consumers (the Blazor page and
/// `eval agent`) must treat this event as "discard what you accumulated and show this".
/// </summary>
public sealed record AnswerReplaced(string Text) : ChatEvent;

/// <summary>Raised once per answer, after the last <see cref="TextDelta"/>, only when the stream completed.</summary>
public sealed record AuditReported(AnswerAudit Audit) : ChatEvent;

public static class AnswerAuditor
{
    /// <param name="instructions">The agent's system prompt, to detect a leak. Null skips that check.</param>
    public static AnswerAudit Audit(string question, string answer, CitationRegistry registry, string? instructions = null)
    {
        var citations = registry.Resolve(answer);
        var sources = registry.Snapshot();
        // The question and "2025" (the corpus year, stated in the instructions) may legitimately be echoed by the answer.
        var grounding = GroundednessChecker.Check(answer, sources, question + " 2025");
        var leaks = instructions is not null && InstructionLeakDetector.Check(answer, instructions).Leaked;
        return new AnswerAudit(citations, grounding, GroundednessChecker.HasDisclaimer(answer), AnswerChecks.LooksLikeRefusal(answer), leaks);
    }
}
