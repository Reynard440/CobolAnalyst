namespace CobolAnalyst.Web.Core.Llm;

/// <summary>Streams completions from a local Ollama LLM instance.</summary>
public interface ILlmClient
{
    /// <summary>The model tag currently selected for inference (e.g. "qwen2.5-coder:32b").</summary>
    string SelectedModel { get; set; }

    /// <summary>
    /// Sends <paramref name="prompt"/> to the selected model and yields decoded text tokens
    /// as they arrive from the streaming response.
    /// </summary>
    IAsyncEnumerable<string> StreamCompletionAsync(
        string prompt,
        CancellationToken cancellationToken = default);

    /// <summary>Returns the list of model tags available in the local Ollama instance.</summary>
    Task<IReadOnlyList<string>> GetAvailableModelsAsync(
        CancellationToken cancellationToken = default);
}
