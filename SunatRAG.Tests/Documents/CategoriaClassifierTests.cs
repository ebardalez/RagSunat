using SunatRAG.Core.Documents;

namespace SunatRAG.Tests.Documents;

/// <summary>Every sumilla below is a real one from the 2025 corpus.</summary>
public class CategoriaClassifierTests
{
    [Theory]
    [InlineData("Deja sin efecto designaciones y encargaturas y encarga en cargos de confianza")]
    [InlineData("Deja sin efecto y designa representante de la SUNAT ante la Comisión Especial que representa al Estado")]
    [InlineData("Designa en cargo de confianza")]
    [InlineData("Designan representantes de la SUNAT ante la Comisión Especial que representa al Estado en Controversias Internacionales")]
    [InlineData("Encarga cargo de confianza")]
    [InlineData("Autoriza viaje de servidores de la SUNAT a la ciudad de Bruselas")]
    [InlineData("Viaje de servidores de la SUNAT para asistir a la 92 sesión de la Comisión Política")]
    [InlineData("Aprueba modificación del Contrato N.º 286-2022/SUNAT - Prestación de Servicios (Derivado del Concurso Público N.º 00015-2022-SUNAT/7R0100)")]
    [InlineData("Declara la nulidad de oficio del Contrato N.° 061-2025/SUNAT - Prestación de Servicios")]
    [InlineData("Aprueba desagregación de los recursos aprobados mediante Decreto Supremo N.º 334-2025-EF")]
    [InlineData("Aprueba el Plan Operativo Institucional Anual 2026 consistente con el Presupuesto Institucional de Apertura 2026")]
    [InlineData("Aprueba la modificación del Plan de implementación del teletrabajo en la Superintendencia")]
    [InlineData("Aprueba el set de indicadores y proyectos de la Superintendencia Nacional de Aduanas y de Administración Tributaria para el año 2026")]
    [InlineData("Conforma Comisión de Programación Multianual Presupuestaria y Formulación Presupuestaria 2026-2028 de la SUNAT")]
    [InlineData("Autoriza transferencia financiera a favor de la Unidad Ejecutora 002-1698")]
    public void Administrative_housekeeping_is_classified_as_Administrativa(string sumilla)
    {
        var result = CategoriaClassifier.Classify(TipoNorma.ResolucionSuperintendencia, sumilla);

        Assert.Equal(Categoria.Administrativa, result.Categoria);
        Assert.NotEqual("default", result.Rule);
    }

    [Theory]
    [InlineData("Resolución de Superintendencia que designa y excluye agentes de retención del Impuesto General a las Ventas")]
    [InlineData("Designa y excluye agentes de retención del Impuesto General a las Ventas")]
    [InlineData("Designan agentes de percepción del Impuesto General a las Ventas")]
    [InlineData("Conforma Comité Revisor a que se refiere el artículo 62-C del Código Tributario")]
    [InlineData("Aprueba el Procedimiento de Adjudicación, Donación y Destino de Bienes - Versión 2")]
    [InlineData("Dispone publicación de Proyecto de Resolución de Superintendencia que aprueba el Procedimiento General")]
    [InlineData("Modifican la relación de puestos de control obligatorios de bienes fiscalizados que pueden ser utilizados")]
    [InlineData("Establece la forma, plazo y condiciones para el acogimiento a la exoneración del IGV")]
    [InlineData("Deja sin efecto resolución")]
    [InlineData("Aprueban el Procedimiento Específico \"Garantías de Operadores de Comercio Exterior\" RECA-PE.03.04 (versión 3)")]
    public void Tax_content_is_never_hidden(string sumilla)
    {
        var result = CategoriaClassifier.Classify(TipoNorma.ResolucionSuperintendencia, sumilla);

        Assert.Equal(Categoria.Normativa, result.Categoria);
    }

    // Found by `scrape --index-only --from 2022 --to 2024` (2026-11-03): housekeeping the 2025-only rules did not know.
    [Theory]
    [InlineData("Aprueba incorporación de mayores ingresos públicos en el Presupuesto Institucional del Año Fiscal 2022 de la Superintendencia Nacional de Aduanas y de Administración Tributaria", "presupuesto")]
    [InlineData("Aprueba la Contratación Directa N° 012-2022-SUNAT/8B7200 \"Servicio de capacitación virtual: Curso Análisis de la Aplicación de los CDI (0587)\"", "contratacion:directa")]
    [InlineData("Aprueba prestación adicional en el marco del Contrato N.° 81-2023/SUNAT - Prestación de Servicios", "contratacion:directa")]
    [InlineData("Aprueban la ampliación del horizonte temporal del Plan Estratégico Institucional (PEI) de la SUNAT para el período 2018-2025", "planeamiento:pei")]
    [InlineData("Formaliza constitución del Comité de Administración del Fondo de Asistencia y Estímulo de los Trabajadores de la Superintendencia", "organizacion:comite-conformacion")]
    [InlineData("Prórroga de la vigencia del mandato del Comité de Administración del Fondo de Asistencia y Estímulo de los Trabajadores", "organizacion:fondo-asistencia")]
    [InlineData("Deja sin efecto y encarga el ejercicio provisional de la presidencia de la junta de acreedores de los clubes de futbol profesional concursados", "personal:deja-sin-efecto")]
    [InlineData("Modifica la relación de trabajadores autorizados a solicitar transferencia de fondos y revocatoria de la orden de pago de cheques", "tesoreria:servidores-autorizados")]
    [InlineData("Aprueba norma que establece los niveles de autorización para realizar compromisos y conformidad previa al pago con cargo al presupuesto institucional", "presupuesto")]
    [InlineData("Modifica la conformación del Comité de Gobierno y Transformación Digital de la Superintendencia Nacional de Aduanas y de Administración Tributaria", "organizacion:comite-conformacion")]
    [InlineData("Deja sin efecto la conformación del Comité de Activos y Pasivos de la SUNAT", "organizacion:comite-conformacion")]
    public void Housekeeping_found_in_other_years_is_classified_as_Administrativa(string sumilla, string expectedRule)
    {
        var result = CategoriaClassifier.Classify(TipoNorma.ResolucionSuperintendencia, sumilla);

        Assert.Equal(Categoria.Administrativa, result.Categoria);
        Assert.Equal(expectedRule, result.Rule);
    }

    [Theory]
    [InlineData("Modifican la conformación del Comité Revisor a que se refiere el artículo 62-C del Código Tributario")]
    [InlineData("Ratifica a los miembros del Comité Revisor a que se refiere el artículo 62-C del Código Tributario")]
    [InlineData("Modifica la normativa sobre guías de remisión y designa emisores electrónicos")]
    [InlineData("Aprueba el formulario virtual para declarar el Impuesto General a las Ventas")]
    [InlineData("Aprueba disposiciones sobre la contratación electrónica de servicios de comprobantes de pago")]
    public void The_new_rules_do_not_hide_real_tax_norms(string sumilla)
    {
        Assert.Equal(Categoria.Normativa, CategoriaClassifier.Classify(TipoNorma.ResolucionSuperintendencia, sumilla).Categoria);
    }

    [Fact]
    public void Informes_are_always_Normativa_even_if_the_text_looks_administrative()
    {
        var result = CategoriaClassifier.Classify(TipoNorma.Informe, "Designa en cargo de confianza");

        Assert.Equal(Categoria.Normativa, result.Categoria);
        Assert.Equal("tipo:no-resolucion", result.Rule);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Missing_sumilla_defaults_to_Normativa(string? sumilla)
    {
        Assert.Equal(Categoria.Normativa, CategoriaClassifier.Classify(TipoNorma.ResolucionSuperintendencia, sumilla).Categoria);
    }

    [Fact]
    public void Matching_ignores_case_accents_and_extra_spaces()
    {
        var result = CategoriaClassifier.Classify(TipoNorma.ResolucionSuperintendencia, "  DESIGNA   EN  cargo de confianza ");

        Assert.Equal(Categoria.Administrativa, result.Categoria);
        Assert.Equal("personal:designacion", result.Rule);
    }
}
