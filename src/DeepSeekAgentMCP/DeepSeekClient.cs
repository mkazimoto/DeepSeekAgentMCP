using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using DeepSeekAgentMCP.Models;

namespace DeepSeekAgentMCP;

/// <summary>
/// HTTP client for the DeepSeek Chat Completions API.
/// </summary>
public class DeepSeekClient : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly string _apiKey;
    private readonly string _model;
    private readonly int _maxTokens;
    private readonly double _temperature;

    private const string BaseUrl = "https://api.deepseek.com";

    public DeepSeekClient(string apiKey, string model = "deepseek-chat", int maxTokens = 4096, double temperature = 0.7)
    {
        _apiKey = apiKey;
        _model = model;
        _maxTokens = maxTokens;
        _temperature = temperature;

        _httpClient = new HttpClient
        {
            BaseAddress = new Uri(BaseUrl),
            Timeout = TimeSpan.FromMinutes(5)
        };
        _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {_apiKey}");
    }

    /// <summary>
    /// Sends a chat completion request to DeepSeek (non-streaming).
    /// </summary>
    public async Task<DeepSeekChatResponse> SendChatAsync(
        List<ChatMessage> messages,
        List<ToolDefinition>? tools = null,
        string? toolChoice = null,
        CancellationToken cancellationToken = default)
    {
        var request = new DeepSeekChatRequest
        {
            Model = _model,
            Messages = messages,
            MaxTokens = _maxTokens,
            Temperature = _temperature,
            Stream = false,
            Tools = tools,
            ToolChoice = toolChoice
        };

        var json = JsonSerializer.Serialize(request);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        var response = await _httpClient.PostAsync("/chat/completions", content, cancellationToken);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<DeepSeekChatResponse>(cancellationToken: cancellationToken);

        return result ?? throw new InvalidOperationException("Failed to deserialize DeepSeek response.");
    }

    /// <summary>
    /// Sends a chat completion request with streaming support.
    /// </summary>
    public async IAsyncEnumerable<string> SendChatStreamingAsync(
        List<ChatMessage> messages,
        List<ToolDefinition>? tools = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var request = new DeepSeekChatRequest
        {
            Model = _model,
            Messages = messages,
            MaxTokens = _maxTokens,
            Temperature = _temperature,
            Stream = true,
            Tools = tools
        };

        var json = JsonSerializer.Serialize(request);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        var httpRequest = new HttpRequestMessage(HttpMethod.Post, "/chat/completions")
        {
            Content = content
        };

        var response = await _httpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);

        while (true)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null) break;
            if (line.Length == 0) continue;

            if (!line.StartsWith("data: ")) continue;

            var data = line[6..];
            if (data == "[DONE]") yield break;

            if (TryParseStreamingChunk(data, out var chunkContent) && chunkContent != null)
            {
                yield return chunkContent;
            }
        }
    }

    /// <summary>
    /// Attempts to parse a streaming SSE chunk and extract the content.
    /// </summary>
    private static bool TryParseStreamingChunk(string data, out string? content)
    {
        content = null;
        try
        {
            using var doc = JsonDocument.Parse(data);
            var choice = doc.RootElement.GetProperty("choices")[0];

            if (choice.TryGetProperty("delta", out var delta))
            {
                if (delta.TryGetProperty("content", out var contentProp) && contentProp.ValueKind == JsonValueKind.String)
                {
                    content = contentProp.GetString();
                    return true;
                }
            }
        }
        catch (JsonException)
        {
            // Skip malformed chunks
        }
        return false;
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }
}


