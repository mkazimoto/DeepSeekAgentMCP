using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using DeepSeekAgentMCP.Models;
using Microsoft.Extensions.Logging;

namespace DeepSeekAgentMCP;

/// <summary>
/// HTTP client for the DeepSeek Chat Completions API.
/// </summary>
public class DeepSeekClient : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly string _apiKey;
    private readonly string _model;
    private readonly int _maxTokens;
    private readonly double _temperature;
    private readonly ThinkingConfig? _thinking;
    private readonly string? _reasoningEffort;
    private readonly ILogger<DeepSeekClient>? _logger;

    private const string BaseUrl = "https://api.deepseek.com";

    public DeepSeekClient(string apiKey, string model = "deepseek-v4-flash", int maxTokens = 4096, double temperature = 0.3, ThinkingConfig? thinking = null, string? reasoningEffort = null, ILogger<DeepSeekClient>? logger = null, int httpClientTimeoutSeconds = 300, HttpClient? httpClient = null)
    {
        _apiKey = apiKey;
        _model = model;
        _maxTokens = maxTokens;
        _temperature = temperature;
        _thinking = thinking;
        _reasoningEffort = reasoningEffort;
        _logger = logger;

        if (httpClient != null)
        {
            _httpClient = httpClient;
            _ownsHttpClient = false;
        }
        else
        {
            _httpClient = new HttpClient
            {
                BaseAddress = new Uri(BaseUrl),
                Timeout = TimeSpan.FromSeconds(httpClientTimeoutSeconds)
            };
            _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {_apiKey}");
            _ownsHttpClient = true;
        }

        _logger?.LogInformation("DeepSeekClient initialized (model: {Model}, maxTokens: {MaxTokens}, temperature: {Temperature}, timeout: {Timeout}s)",
            model, maxTokens, temperature, httpClientTimeoutSeconds);
    }

    /// <summary>
    /// Sends a chat completion request to DeepSeek (non-streaming) with retry on transient failures.
    /// </summary>
    public virtual async Task<DeepSeekChatResponse> SendChatAsync(
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
            //Temperature = _temperature,
            Stream = false,
            Tools = tools,
            ToolChoice = toolChoice,
            Thinking = _thinking,
            ReasoningEffort = _reasoningEffort
        };

        _logger?.LogDebug("Sending chat request (model: {Model}, messages: {Count}, tools: {ToolCount})",
            _model, messages.Count, tools?.Count ?? 0);

        // Retry with exponential backoff + jitter on 429 and 5xx
        const int maxRetries = 3;
        var baseDelayMs = 1000;

        for (var attempt = 1; attempt <= maxRetries; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                // Create fresh content each attempt to avoid issues with consumed HttpContent streams
                var json = JsonSerializer.Serialize(request);
                using var content = new StringContent(json, Encoding.UTF8, "application/json");

                var response = await _httpClient.PostAsync("/chat/completions", content, cancellationToken);

                if (response.IsSuccessStatusCode)
                {
                    var result = await response.Content.ReadFromJsonAsync<DeepSeekChatResponse>(cancellationToken: cancellationToken);
                    if (result?.Usage != null)
                    {
                        _logger?.LogInformation("DeepSeek response OK (tokens: {PromptTokens} prompt + {CompletionTokens} completion = {TotalTokens} total)",
                            result.Usage.PromptTokens, result.Usage.CompletionTokens, result.Usage.TotalTokens);
                    }
                    return result ?? throw new InvalidOperationException("Failed to deserialize DeepSeek response.");
                }

                // Retry on 429 (rate limit) and 5xx (server errors)
                if (attempt < maxRetries && ((int)response.StatusCode == 429 || (int)response.StatusCode >= 500))
                {
                    // Exponential backoff with jitter (±25% randomization)
                    var delayMs = baseDelayMs * Math.Pow(2, attempt - 1);
                    var jitter = delayMs * 0.25 * (Random.Shared.NextDouble() * 2 - 1);
                    var delay = TimeSpan.FromMilliseconds(delayMs + jitter);

                    _logger?.LogWarning("DeepSeek retry {Attempt}/{MaxRetries} after {StatusCode} — waiting {Delay}ms",
                        attempt, maxRetries, (int)response.StatusCode, delay.TotalMilliseconds);

                    await Task.Delay(delay, cancellationToken);
                    continue;
                }

                // Non-retryable error
                response.EnsureSuccessStatusCode();
            }
            catch (HttpRequestException) when (attempt < maxRetries)
            {
                var delayMs = baseDelayMs * Math.Pow(2, attempt - 1);
                var jitter = delayMs * 0.25 * (Random.Shared.NextDouble() * 2 - 1);
                var delay = TimeSpan.FromMilliseconds(delayMs + jitter);

                _logger?.LogWarning("DeepSeek retry {Attempt}/{MaxRetries} after HttpRequestException — waiting {Delay}ms",
                    attempt, maxRetries, delay.TotalMilliseconds);

                await Task.Delay(delay, cancellationToken);
            }
        }

        _logger?.LogError("Failed to get response from DeepSeek after {MaxRetries} retries", maxRetries);
        throw new HttpRequestException($"Failed to get response from DeepSeek after {maxRetries} retries.");
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
            //Temperature = _temperature,
            Stream = true,
            Tools = tools,
            Thinking = _thinking,
            ReasoningEffort = _reasoningEffort
        };

        var json = JsonSerializer.Serialize(request);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        _logger?.LogDebug("Sending streaming chat request (model: {Model}, messages: {Count})", _model, messages.Count);

        var httpRequest = new HttpRequestMessage(HttpMethod.Post, "/chat/completions")
        {
            Content = content
        };

        var response = await _httpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        _logger?.LogInformation("DeepSeek streaming connection established");

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
        if (_ownsHttpClient)
            _httpClient.Dispose();
    }
}



// Polly test
