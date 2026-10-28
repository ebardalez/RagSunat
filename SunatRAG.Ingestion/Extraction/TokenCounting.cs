using Microsoft.ML.Tokenizers;

namespace SunatRAG.Ingestion.Extraction;

/// <summary>
/// "How many tokens does the EMBEDDING model see for this text?" Behind an interface so the chunker is testable
/// without a 5 MB model file, and so the tokenizer can follow the embedding model if it changes.
/// </summary>
public interface ITokenCounter
{
    int Count(string text);
}

/// <summary>
/// Real bge-m3 vocabulary (XLM-RoBERTa SentencePiece). The model file ships with the bge-m3 repo on Hugging Face
/// (sentencepiece.bpe.model); Microsoft.ML.Tokenizers loads it directly. BOS/EOS are not counted: they are a constant +2
/// that the embedding runtime adds itself.
/// </summary>
public sealed class SentencePieceTokenCounter : ITokenCounter
{
    private readonly Tokenizer tokenizer;

    public SentencePieceTokenCounter(string modelPath)
    {
        using var stream = File.OpenRead(modelPath);
        tokenizer = SentencePieceTokenizer.Create(stream, false, false); // (addBeginOfSentence, addEndOfSentence)
    }

    public int Count(string text) => string.IsNullOrEmpty(text) ? 0 : tokenizer.CountTokens(text);
}

/// <summary>
/// ~3.5 characters per token: a rough figure for Spanish with XLM-R vocabulary. For tests and dry runs ONLY;
/// the token math that decides how many chunks fit in Ollama's context window needs the real tokenizer.
/// </summary>
public sealed class ApproximateTokenCounter : ITokenCounter
{
    public int Count(string text) => string.IsNullOrEmpty(text) ? 0 : (int)Math.Ceiling(text.Length / 3.5);
}
