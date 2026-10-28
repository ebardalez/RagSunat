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
