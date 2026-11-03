using SunatRAG.Core.Documents;
using SunatRAG.Ingestion.Scraping;

namespace SunatRAG.Tests.Scraping;

public class RuleAuditTests
{
    private static NormaListing Res(int year, string numero, string? sumilla, TipoNorma tipo = TipoNorma.ResolucionSuperintendencia) =>
        new($"id-{year}-{numero}", tipo, year, numero, $"Resolución {numero}", sumilla, null, null, new Uri($"https://sunat.test/{numero}.pdf"), []);

    [Fact]
    public void Counts_per_year_split_resolutions_from_informes_and_apply_the_classifier()
    {
        var audit = RuleAudit.Analyze(
        [
            Res(2024, "000001-2024", "Designan Intendente Regional de Lima"),                     // administrativa: personal:designacion
            Res(2024, "000002-2024", "Autorizan viaje de servidores a Panamá"),                   // administrativa: viaje
            Res(2024, "000003-2024", "Modifican el Reglamento de Comprobantes de Pago"),          // normativa
            Res(2024, "I000010-2024", "Impuesto a la Renta de no domiciliados", TipoNorma.Informe),
            Res(2023, "000009-2023", "Aprueban formulario virtual para declarar el IGV")          // another year: its own row
        ]);

        var y2024 = Assert.Single(audit, a => a.Year == 2024);
        Assert.Equal(3, y2024.Resoluciones);
        Assert.Equal(1, y2024.Informes);
        Assert.Equal(2, y2024.Administrativas);
        Assert.Equal(66.7, y2024.AdministrativePercent, precision: 1);
        Assert.Equal([2023, 2024], audit.Select(a => a.Year)); // ordered by year
    }

    [Fact]
    public void A_normativa_sumilla_with_administrative_wording_is_flagged_as_a_possible_missed_rule()
    {
        // No rule matches this sumilla, but "convenio" / "capacitación" are administrative wording: a human should look.
        var audit = RuleAudit.Analyze([Res(2024, "000020-2024", "Aprueban convenio de capacitación con universidades")]);

        var y = Assert.Single(audit);
        Assert.Equal(1, y.PossiblyMissedTotal);
        Assert.Equal("000020-2024", Assert.Single(y.PossiblyMissed).Numero);
        Assert.Empty(y.PossiblyWrong);
    }

    [Fact]
    public void An_administrativa_sumilla_with_tax_wording_is_flagged_as_the_costly_error()
    {
        // The rule fires on the first words, but the sumilla is about retention agents: that would hide a real tax norm.
        var audit = RuleAudit.Analyze([Res(2024, "000030-2024", "Designan comité para resolver reclamos sobre retenciones del Impuesto a la Renta")]);

        var y = Assert.Single(audit);
        Assert.Equal(1, y.Administrativas);
        Assert.Equal(1, y.PossiblyWrongTotal);
        Assert.Equal("000030-2024", Assert.Single(y.PossiblyWrong).Numero);
    }

    [Fact]
    public void The_entitys_own_name_is_not_tax_wording()
    {
        // "Administración Tributaria" is part of the SUNAT's name: this travel authorization is correctly administrative, not suspicious.
        var audit = RuleAudit.Analyze(
            [Res(2024, "000050-2024", "Autoriza viaje de servidor de la Superintendencia Nacional de Aduanas y de Administración Tributaria para participar en un taller")]);

        var y = Assert.Single(audit);
        Assert.Equal(1, y.Administrativas);
        Assert.Equal(0, y.PossiblyWrongTotal);
    }

    [Fact]
    public void Samples_are_capped_but_the_totals_are_not()
    {
        var listings = Enumerable.Range(1, 40).Select(i => Res(2024, $"{i:D6}-2024", "Aprueban convenio marco"));

        var y = Assert.Single(RuleAudit.Analyze(listings, sampleSize: 10));

        Assert.Equal(40, y.PossiblyMissedTotal);
        Assert.Equal(10, y.PossiblyMissed.Count);
    }

    [Fact]
    public void Missing_sumillas_do_not_crash_the_audit()
    {
        var y = Assert.Single(RuleAudit.Analyze([Res(2024, "000040-2024", null)]));

        Assert.Equal(0, y.Administrativas);
        Assert.Equal(0, y.PossiblyMissedTotal);
    }
}
