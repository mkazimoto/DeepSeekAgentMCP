using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DeepSeekAgentMCP;
using DeepSeekAgentMCP.Models;

namespace DeepSeekAgentMCP.Tests;

public class DeepSeekClientTests
{
    /// <summary>
    /// A delegating handler that returns a pre-configured response for testing.
    /// </summary>
    private class MockHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _handler;

        public MockHttpMessageHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler)
        {
            _handler = handler;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return await _handler(request);
        }
    }

    private static DeepSeekChatResponse CreateFakeResponse(string content, int promptTokens = 10, int completionTokens = 20)
    {
        return new DeepSeekChatResponse
        {
            Id = "test-id",
            Object = "chat.completion",
            Created = 1234567890,
            Model = "deepseek-v4-flash",
            Choices =
            [
                new Choice
                {
                    Index = 0,
                    Message = new ChatMessage
                    {
                        Role = "assistant",
                        Content = content
                    },
                    FinishReason = "stop"
                }
            ],
            Usage = new Usage
            {
                PromptTokens = promptTokens,
                CompletionTokens = completionTokens,
                TotalTokens = promptTokens + completionTokens
            }
        };
    }

    private static DeepSeekChatResponse CreateFakeResponseWithToolCalls(List<ToolCall> toolCalls)
    {
        return new DeepSeekChatResponse
        {
            Id = "test-id",
            Object = "chat.completion",
            Created = 1234567890,
            Model = "deepseek-v4-flash",
            Choices =
            [
                new Choice
                {
                    Index = 0,
                    Message = new ChatMessage
                    {
                        Role = "assistant",
                        Content = null,
                        ToolCalls = toolCalls
                    },
                    FinishReason = "tool_calls"
                }
            ],
            Usage = new Usage { PromptTokens = 10, CompletionTokens = 5, TotalTokens = 15 }
        };
    }

    [Fact]
    public async Task SendChatAsync_SuccessfulResponse_ReturnsResult()
    {
        // Arrange
        var expectedContent = "Hello! How can I help you?";
        var fakeResponse = CreateFakeResponse(expectedContent);
        var json = JsonSerializer.Serialize(fakeResponse);

        var httpClient = new HttpClient(new MockHttpMessageHandler(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
            })))
        {
            BaseAddress = new Uri("https://api.deepseek.com")
        };

        var client = CreateClientWithMockHttpClient(httpClient);

        // Act
        var result = await client.SendChatAsync(
            [new ChatMessage { Role = "user", Content = "Hello" }]);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(expectedContent, result.Choices[0].Message.Content);
        Assert.Equal(30, result.Usage?.TotalTokens);
    }

    [Fact]
    public async Task SendChatAsync_RetryOn429_EventuallySucceeds()
    {
        // Arrange
        var attemptCount = 0;
        var expectedContent = "Success after retry";

        var httpClient = new HttpClient(new MockHttpMessageHandler(async _ =>
        {
            attemptCount++;
            if (attemptCount <= 2)
            {
                return new HttpResponseMessage((HttpStatusCode)429)
                {
                    Headers = { { "Retry-After", "1" } }
                };
            }
            var fakeResponse = CreateFakeResponse(expectedContent);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(fakeResponse),
                    System.Text.Encoding.UTF8, "application/json")
            };
        }))
        { BaseAddress = new Uri("https://api.deepseek.com") };

        var client = CreateClientWithMockHttpClient(httpClient);

        // Act
        var result = await client.SendChatAsync(
            [new ChatMessage { Role = "user", Content = "Hello" }]);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(expectedContent, result.Choices[0].Message.Content);
        Assert.True(attemptCount >= 3, $"Expected at least 3 attempts, got {attemptCount}");
    }

    [Fact]
    public async Task SendChatAsync_RetryOn5xx_EventuallySucceeds()
    {
        // Arrange
        var attemptCount = 0;

        var httpClient = new HttpClient(new MockHttpMessageHandler(async _ =>
        {
            attemptCount++;
            if (attemptCount <= 2)
            {
                return new HttpResponseMessage(HttpStatusCode.InternalServerError);
            }
            var fakeResponse = CreateFakeResponse("OK");
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(fakeResponse),
                    System.Text.Encoding.UTF8, "application/json")
            };
        }))
        { BaseAddress = new Uri("https://api.deepseek.com") };

        var client = CreateClientWithMockHttpClient(httpClient);

        // Act
        var result = await client.SendChatAsync(
            [new ChatMessage { Role = "user", Content = "Hello" }]);

        // Assert
        Assert.NotNull(result);
        Assert.True(attemptCount >= 3);
    }

    [Fact]
    public async Task SendChatAsync_NonRetryable4xx_Throws()
    {
        // Arrange
        var httpClient = new HttpClient(new MockHttpMessageHandler(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("{\"error\":\"bad request\"}")
            })))
        { BaseAddress = new Uri("https://api.deepseek.com") };

        var client = CreateClientWithMockHttpClient(httpClient);

        // Act & Assert
        await Assert.ThrowsAsync<HttpRequestException>(async () =>
            await client.SendChatAsync(
                [new ChatMessage { Role = "user", Content = "Hello" }]));
    }

    [Fact]
    public async Task SendChatAsync_WithTools_SendsCorrectRequest()
    {
        // Arrange
        HttpRequestMessage? capturedRequest = null;
        var fakeResponse = CreateFakeResponse("Using tools");

        var httpClient = new HttpClient(new MockHttpMessageHandler(async req =>
        {
            capturedRequest = req;
            var body = await req.Content!.ReadAsStringAsync();
            // Verify the request contains tools
            Assert.Contains("tools", body);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(fakeResponse),
                    System.Text.Encoding.UTF8, "application/json")
            };
        }))
        { BaseAddress = new Uri("https://api.deepseek.com") };

        var client = CreateClientWithMockHttpClient(httpClient);

        var tools = new List<ToolDefinition>
        {
            new()
            {
                Function = new ToolFunction
                {
                    Name = "test_tool",
                    Description = "A test tool",
                    Parameters = null
                }
            }
        };

        // Act
        var result = await client.SendChatAsync(
            [new ChatMessage { Role = "user", Content = "Use tools" }],
            tools: tools);

        // Assert
        Assert.NotNull(result);
        Assert.NotNull(capturedRequest);
        Assert.Equal("/chat/completions", capturedRequest.RequestUri?.AbsolutePath);
    }

    /// <summary>
    /// Creates a DeepSeekClient with a mock HttpClient injected via the constructor.
    /// </summary>
    private static DeepSeekClient CreateClientWithMockHttpClient(HttpClient httpClient, string apiKey = "test-api-key")
    {
        return new DeepSeekClient(apiKey, httpClient: httpClient);
    }
}
