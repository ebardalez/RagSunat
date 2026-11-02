using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using SunatRAG.Core.Agents;

namespace SunatRAG.Ingestion.Commands;

/// <summary>
/// Phase 5: run the agent headless, so latency, tool calls and token usage can be measured without a browser.
/// Several questions in one invocation share one session (one conversation), which is how follow-ups and history compaction get exercised.
/// Override agent settings with env vars, e.g. $env:Agent__Retrieval="ContextProvider"; $env:Agent__Reasoning="Low".
/// </summary>
public static class AskCommand
{
    public const string Usage = "Uso: dotnet run --project SunatRAG.Ingestion -- ask \"pregunta 1\" [\"pregunta 2\" ...]   (varias preguntas = una conversación)";

    public static async Task<int> RunAsync(IServiceProvider sp, string[] args, CancellationToken ct)
    {
        var questions = args.Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToList();
        if (questions.Count == 0)
        {
            Console.Error.WriteLine(Usage);
            return 1;
        }

        Console.OutputEncoding = Encoding.UTF8;
        using var scope = sp.CreateScope();
        var chat = scope.ServiceProvider.GetRequiredService<SunatChatSession>();

        foreach (var question in questions)
        {
            Console.WriteLine($"\n=== {question}");
            var total = Stopwatch.StartNew();
            TimeSpan? firstToken = null;
            var answer = new StringBuilder();
            var calls = 0;
            long lastPrompt = 0, output = 0;

            await foreach (var ev in chat.AskAsync(question, ct))
            {
                switch (ev)
                {
                    case ToolCalled t:
                        Console.WriteLine($"  [{total.Elapsed.TotalSeconds,5:0.0}s] herramienta: {t.Name} {t.Arguments}");
                        break;
                    case UsageReported u:
                        calls++;
                        lastPrompt = u.InputTokens ?? lastPrompt;
                        output += u.OutputTokens ?? 0;
                        Console.WriteLine($"  [{total.Elapsed.TotalSeconds,5:0.0}s] llamada al modelo #{calls}: prompt {u.InputTokens} tok, salida {u.OutputTokens} tok");
                        break;
                    case TextDelta d:
                        firstToken ??= total.Elapsed;
                        answer.Append(d.Text);
                        break;
                }
            }

            Console.WriteLine($"\n{answer}\n");
            var sources = chat.Citations.Resolve(answer.ToString());
            foreach (var c in sources.Valid) Console.WriteLine($"  fuente [{c.Number}] {c.Cita}  {c.PageUrl}");
            if (sources.Invalid.Count > 0) Console.WriteLine($"  CITAS INVENTADAS: {string.Join(", ", sources.Invalid)}");
            if (sources.Valid.Count == 0 && sources.Invalid.Count == 0) Console.WriteLine("  (la respuesta no cita ninguna fuente)");
            Console.WriteLine(
                $"  total {total.Elapsed.TotalSeconds:0.0}s, primer texto a los {firstToken?.TotalSeconds.ToString("0.0") ?? "n/d"}s, " +
                $"{calls} llamada(s), último prompt {lastPrompt} tok, salida {output} tok");
        }

        return 0;
    }
}
