namespace SunatRAG.Core.Configuration;

/// <summary>Settings for the retrieval guardrail (section "Guard").</summary>
public sealed class GuardOptions
{
    public const string SectionName = "Guard";

    /// <summary>Turn the guard off to see what the model does with weak sources (useful as an eval baseline).</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// Best cosine distance beyond which retrieval is treated as "nothing relevant" and NO chunks reach the model.
    /// Calibrated with `eval retrieval` on the 2025 golden set (28 cases), Tool strategy (model-rewritten query): 0.42 is the lowest threshold
    /// that keeps 100 % of the answerable questions (the closest one sits at 0.403, so the margin is thin) and refuses every off-topic one.
    /// With the ContextProvider strategy (raw user text) the same data needs 0.46. It CANNOT reject in-domain questions the corpus lacks
    /// ("tasa del IGV" sits at 0.40): those are the model's and the answer audit's job. Re-run the sweep after re-indexing or changing models.
    /// </summary>
    public double MaxBestDistance { get; init; } = 0.46; // ContextProvider (raw user text) needs 0.46; the Tool strategy (model-rewritten query) would need 0.42
}
