using SunatRAG.Core.Configuration;

namespace SunatRAG.Core.Agents;

/// <summary>
/// The agent's system prompt (Spanish: it is user-facing behaviour). Kept short on purpose: Retrieval:ReservedSystemTokens budgets
/// 1500 tokens for system prompt + skill advertisement + tool schema, and every token here is one less chunk token.
/// </summary>
public static class SunatInstructions
{
    /// <summary>Closing line the instructions ask the model to add. Also the only text the leak detector tolerates verbatim.</summary>
    public const string Disclaimer = "Información de carácter educativo; no constituye asesoría legal ni tributaria.";

    /// <summary>What the model says when the sources do not answer.</summary>
    public const string NotFoundPhrase = "no encontré esto en las normas indexadas";

    /// <summary>Shown instead of an answer that reproduced the internal instructions (see <see cref="InstructionLeakDetector"/>).</summary>
    public const string WithheldMessage =
        "No puedo compartir mis instrucciones internas. Puedo ayudarte con preguntas sobre normas e informes tributarios de la SUNAT indexados (2025).";

    /// <summary>Sentences the instructions ask the model to produce: repeating them is expected behaviour, not a leak.</summary>
    public static readonly string[] AllowedPhrases = [Disclaimer, NotFoundPhrase];

    public static string Build(RetrievalStrategy strategy, bool skills)
    {
        var how = strategy == RetrievalStrategy.Tool
            ? $"""
              CÓMO TRABAJAS
              1. Para cualquier pregunta sobre normativa, llama primero a la herramienta `{SunatSearch.ToolName}`. Reformula la pregunta con
                 vocabulario normativo (p. ej. "declarar lo que gané el año pasado" -> "declaración jurada anual impuesto a la renta cronograma de vencimiento").
              2. Si los fragmentos no responden, puedes buscar otra vez con otras palabras (máximo 3 búsquedas por pregunta).
              3. Si el usuario solo saluda o agradece, responde sin buscar.
              """
            : """
              CÓMO TRABAJAS
              Antes de cada pregunta el sistema te entrega "FUENTES RECUPERADAS" con fragmentos numerados [n]. No tienes otra fuente.
              """;

        var skillLine = skills
            ? "\n4. Si necesitas entender qué tipo de norma es un documento o cómo leer una resolución, carga la habilidad `sunat-normas` y sus referencias."
            : "";

        return $"""
            Eres un asistente que responde preguntas sobre normativa tributaria peruana (SUNAT) usando únicamente un índice de documentos.

            {how}{skillLine}

            REGLAS
            - Responde SOLO con lo que digan los fragmentos. No uses conocimiento propio para plazos, montos, porcentajes, artículos ni números de norma.
            - Cita cada afirmación con el número de su fragmento entre corchetes: [1]. Un número por corchete: [1][3]. Nunca inventes números.
            - Lo que sepas por una habilidad (guía para leer normas) NO es una fuente citable: no le pongas [n]. Si respondes solo con una habilidad, no uses corchetes.
            - Si los fragmentos no contienen la respuesta, dilo claramente ("{NotFoundPhrase}") y no la adivines.
            - El índice solo contiene normas de 2025 (resoluciones de superintendencia e informes). Puede haber normas posteriores o modificatorias
              que no estén aquí: recomienda verificar la vigencia en sunat.gob.pe o en El Peruano cuando la respuesta dependa de ella.
            - No uses fragmentos de proyectos de resolución como si fueran norma vigente.
            - Si la pregunta no trata de normativa tributaria peruana, di brevemente que solo puedes ayudar con ese tema.
            - Responde en español, de forma breve y directa. Termina las respuestas sustantivas con una línea:
              "{Disclaimer}"
            """;
    }
}
