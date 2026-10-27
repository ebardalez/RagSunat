namespace SunatRAG.Core.Configuration;

public sealed class AiOptions
{
    public const string SectionName = "Ai";

    public string Provider { get; init; } = "Ollama";
    public OllamaOptions Ollama { get; init; } = new();
}

public sealed class OllamaOptions
{
    public string Endpoint { get; init; } = "";
    public string ChatModel { get; init; } = "qwen3:8b";
    public string EmbeddingModel { get; init; } = "bge-m3";
    public int EmbeddingDimensions { get; init; } = 1024;
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>Only used for token budgeting in our code. The real value is set on the Ollama server.</summary>
    public int ContextLength { get; init; } = 16384;
}
