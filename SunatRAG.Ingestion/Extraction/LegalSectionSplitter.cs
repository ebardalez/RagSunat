using System.Text.RegularExpressions;

namespace SunatRAG.Ingestion.Extraction;

/// <summary>A run of paragraphs under one heading. <see cref="Heading"/> is a path ("ANEXO 1 &gt; Artículo 2.- Alcance"), null for the preámbulo.</summary>
public sealed record Section(string? Heading, IReadOnlyList<Paragraph> Paragraphs);

/// <summary>
/// Splits a norma into its legal structure so a chunk never starts in the middle of an article if it can help it.
/// Why this matters for RAG: "Artículo 5" retrieved WITH its heading is citable; a 500-token window cut at an arbitrary
/// byte offset is not. Heading lines are only recognised as the FIRST line of a paragraph, which avoids false hits
/// on running text such as "artículo 35 de la Ley del IGV" that happens to start a wrapped line.
/// </summary>
public static class LegalSectionSplitter
{
    private enum Kind { None, Part, Structural, Annex, Minor }

    private const RegexOptions Opts = RegexOptions.Compiled | RegexOptions.CultureInvariant;

    // Document parts: resoluciones (CONSIDERANDO / SE RESUELVE) and informes (MATERIA / BASE LEGAL / ANÁLISIS / CONCLUSIONES).
    private static readonly Regex Part = new(
        @"^(CONSIDERANDO|SE RESUELVE|RESUELVE|MATERIA|BASE LEGAL|AN[ÁA]LISIS|CONCLUSI[OÓ]N(ES)?|RECOMENDACI[OÓ]N(ES)?)\s*:?\s*$", Opts);

    private static readonly Regex Structural = new(
        @"^((T[ÍI]TULO|CAP[ÍI]TULO|SECCI[ÓO]N|LIBRO)\s+([IVXLCDM]+|\d+|PRELIMINAR|[ÚU]NIC[OA])\b|DISPOSICI[ÓO]N(ES)?\s+(COMPLEMENTARIAS?|TRANSITORIAS?|FINALES|FINAL|DEROGATORIAS?|MODIFICATORIAS?)\b)",
        Opts | RegexOptions.IgnoreCase);

    // Only a STANDALONE heading ("ANEXO", "ANEXO I", "ANEXO N° 2"). "Anexo I: Solicitud de ..." at the start of a paragraph is a list item
    // inside a quoted procedure, not the start of an annex.
    private static readonly Regex Annex = new(@"^ANEXO(\s+(N[°º.]?\s*)?([IVXLCDM]+|\d+|[A-Z]))?\s*$", Opts | RegexOptions.IgnoreCase);

    private static readonly Regex Articulo = new(
        @"^(ART[ÍI]CULO|Art[íi]culo)\s+(\d+(-[A-Z])?|[ÚU]NICO|[Úú]nico)\s*[°º]?\s*[.\-–—:]", Opts);

    // "Primera.-", "Segunda.-" ... only meaningful inside a DISPOSICIONES block.
    private static readonly Regex Ordinal = new(
        @"^(Primera|Segunda|Tercera|Cuarta|Quinta|Sexta|S[ée]ptima|Octava|Novena|D[ée]cima(\s+\p{L}+)?|[ÚU]nica)\s*[.\-–—:]", Opts);

    private const int MaxLabelLength = 90;

    public static IReadOnlyList<Section> Split(IReadOnlyList<Paragraph> paragraphs)
    {
        var sections = new List<Section>();
        var current = new List<Paragraph>();
        string? heading = null;
        string? structural = null;

        void Flush()
        {
            if (current.Count > 0) sections.Add(new Section(heading, current.ToArray()));
            current = [];
        }

        foreach (var p in paragraphs)
        {
            var (kind, label) = Classify(p.FirstLine, structural);
            if (kind != Kind.None)
            {
                Flush();
                switch (kind)
                {
                    case Kind.Part:
                        structural = null;
                        heading = label;
                        break;
                    case Kind.Structural:
                        structural = label;
                        heading = label;
                        break;
                    case Kind.Annex:
                        // Articles quoted inside an annex must not inherit "ANEXO V >": the resolution's own articles often resume after it.
                        structural = null;
                        heading = label;
                        break;
                    case Kind.Minor:
                        heading = structural is null ? label : $"{structural} > {label}";
                        break;
                }
            }

            current.Add(p);
        }

        Flush();
        return sections;
    }

    private static (Kind Kind, string Label) Classify(string firstLine, string? structural)
    {
        var line = firstLine.Trim();
        if (Part.IsMatch(line)) return (Kind.Part, line.TrimEnd(':', ' '));
        if (Articulo.IsMatch(line)) return (Kind.Minor, Truncate(line));
        if (Annex.IsMatch(line)) return (Kind.Annex, line.ToUpperInvariant());
        if (line.Length <= 80 && Structural.IsMatch(line)) return (Kind.Structural, Truncate(line));
        if (structural is not null
            && structural.StartsWith("DISPOSICI", StringComparison.OrdinalIgnoreCase)
            && Ordinal.IsMatch(line))
            return (Kind.Minor, Truncate(line));

        return (Kind.None, "");
    }

    private static string Truncate(string s) =>
        s.Length <= MaxLabelLength ? s : s[..(MaxLabelLength - 1)].TrimEnd() + "…";
}
