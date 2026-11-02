namespace SunatRAG.Core.Evaluation;

/// <param name="AnswerableKept">Share of answerable questions whose best cosine distance is within the threshold (not wrongly refused).</param>
/// <param name="UnanswerableRefused">Share of unanswerable questions whose best distance is beyond it (rightly refused).</param>
public sealed record ThresholdPoint(double Threshold, double AnswerableKept, double UnanswerableRefused)
{
    public double BalancedAccuracy => (AnswerableKept + UnanswerableRefused) / 2.0;
}

/// <summary>
/// Picks the "weak match" distance from data instead of by feel. A question is refused when its best cosine distance exceeds the
/// threshold, so there is a trade-off: a low threshold refuses unanswerable questions reliably but also refuses real ones
/// (annoying), a high one never blocks anything (hallucination risk). The sweep shows the whole curve; <see cref="Best"/> is the
/// point with the best balanced accuracy and, among ties, the LOWEST threshold (when in doubt, refuse: this is a legal-adjacent domain).
/// A null distance (nothing retrieved) counts as infinitely far.
/// </summary>
public static class ThresholdCalibrator
{
    public static IReadOnlyList<ThresholdPoint> Sweep(
        IReadOnlyList<double?> answerable, IReadOnlyList<double?> unanswerable, double from, double to, double step)
    {
        if (step <= 0) throw new ArgumentOutOfRangeException(nameof(step));

        var points = new List<ThresholdPoint>();
        // Integer steps avoid accumulating floating-point error in the loop variable.
        var steps = (int)Math.Round((to - from) / step);
        for (var i = 0; i <= steps; i++)
        {
            var t = Math.Round(from + i * step, 4);
            var kept = answerable.Count == 0 ? 0 : answerable.Count(d => d is { } v && v <= t) / (double)answerable.Count;
            var refused = unanswerable.Count == 0 ? 0 : unanswerable.Count(d => d is not { } v || v > t) / (double)unanswerable.Count;
            points.Add(new ThresholdPoint(t, kept, refused));
        }

        return points;
    }

    public static ThresholdPoint Best(IReadOnlyList<ThresholdPoint> sweep) =>
        sweep.Count == 0
            ? throw new ArgumentException("El barrido está vacío.", nameof(sweep))
            : sweep.OrderByDescending(p => p.BalancedAccuracy).ThenBy(p => p.Threshold).First();
}
