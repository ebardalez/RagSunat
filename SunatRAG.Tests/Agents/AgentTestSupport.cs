using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using SunatRAG.Core.Indexing;
using SunatRAG.Core.Retrieval;

namespace SunatRAG.Tests.Agents;

internal static class AgentTestSupport
{
    public static ChunkRecord Record(string id, string doc, string text, int page = 1, string? heading = "Artículo 1.- Plazo", string tipo = "ResolucionSuperintendencia") => new()
    {
        ChunkId = id,
        DocumentId = doc,
        Titulo = $"Resolución de Superintendencia N° {doc}",
        Heading = heading,
        Text = text,
        PageStart = page,
        PageEnd = page,
        Url = $"https://www.sunat.gob.pe/{doc}.pdf",
        Tipo = tipo,
        Categoria = "Normativa",
        Year = 2025
    };

    public static RetrievedChunk Chunk(ChunkRecord r, int rank = 1) =>
        new(r, 1.0 / (60 + rank), rank, rank, 0.31, 5.0, 200);

    public static RetrievalResult Result(string query, double? bestDistance, params RetrievedChunk[] chunks) =>
        new(query, chunks,
            new RetrievalDiagnostics(RetrievalMode.Hybrid, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero,
                chunks.Length, chunks.Length, chunks.Length, bestDistance, 16384, 8760, 200 * chunks.Length, 0, 0, false, false));

    /// <summary>Walks up from the test binaries to the repo root and returns its skills folder (the real SKILL.md files).</summary>
    public static string RepoSkillsPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SunatRAG.slnx"))) dir = dir.Parent;
        return dir is null ? throw new DirectoryNotFoundException("No se encontró la raíz del repo.") : Path.Combine(dir.FullName, "skills");
    }
}

/// <summary>
/// A scripted IChatClient: each model call returns the next scripted content list and the call is recorded.
/// It replaces Ollama in tests, so the whole agent (instructions, providers, tool loop, compaction, streaming) runs for real
/// and only the model's decisions are fixed.
/// </summary>
internal sealed class ScriptedChatClient(params IList<AIContent>[] script) : IChatClient
{
    private int _next;

    public sealed record Call(IReadOnlyList<ChatMessage> Messages, ChatOptions? Options)
    {
        public string AllText => string.Join("\n", Messages.Select(Flatten)) + "\n" + Options?.Instructions;

        /// <summary>ChatMessage.Text only joins TextContent; tool results live in FunctionResultContent and must be included too.</summary>
        public static string Flatten(ChatMessage m) => string.Join(" ", m.Contents.Select(c => c switch
        {
            TextContent t => t.Text,
            FunctionResultContent r => r.Result?.ToString() ?? "",
            _ => ""
        }));
        public IReadOnlyList<string> ToolNames => Options?.Tools?.Select(t => t.Name).ToList() ?? [];
    }

    public List<Call> Calls { get; } = [];

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Calls.Add(new Call(messages.ToList(), options));
        if (_next >= script.Length) throw new InvalidOperationException("El guion del modelo se agotó: hubo más llamadas de las esperadas.");

        var contents = script[_next++];
        contents.Add(new UsageContent(new UsageDetails { InputTokenCount = 1000 + _next, OutputTokenCount = 10 }));
        await Task.Yield();
        yield return new ChatResponseUpdate(ChatRole.Assistant, contents);
    }

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var updates = new List<ChatResponseUpdate>();
        await foreach (var u in GetStreamingResponseAsync(messages, options, cancellationToken)) updates.Add(u);
        return updates.ToChatResponse();
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose() { }
}
