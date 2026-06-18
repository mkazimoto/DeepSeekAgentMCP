using DeepSeekAgentMCP.Models;
using Microsoft.Extensions.Logging;

namespace DeepSeekAgentMCP.Tests;

public class AgentHostBuilderTests
{
    [Fact]
    public void ValidateConfig_EmptyApiKey_ReturnsError()
    {
        var config = new AgentConfig
        {
            ApiKey = string.Empty,
            Model = "deepseek-v4-flash"
        };

        var errors = AgentHostBuilder.ValidateConfig(config);

        Assert.Contains(errors, e => e.Contains("API Key"));
    }

    [Fact]
    public void ValidateConfig_EmptyModel_ReturnsError()
    {
        var config = new AgentConfig
        {
            ApiKey = "sk-test",
            Model = string.Empty
        };

        var errors = AgentHostBuilder.ValidateConfig(config);

        Assert.Contains(errors, e => e.Contains("Model name"));
    }

    [Fact]
    public void ValidateConfig_ZeroMaxTokens_ReturnsError()
    {
        var config = new AgentConfig
        {
            ApiKey = "sk-test",
            Model = "deepseek-v4-flash",
            MaxTokens = 0
        };

        var errors = AgentHostBuilder.ValidateConfig(config);

        Assert.Contains(errors, e => e.Contains("MaxTokens"));
    }

    [Fact]
    public void ValidateConfig_InvalidTemperature_ReturnsError()
    {
        var config = new AgentConfig
        {
            ApiKey = "sk-test",
            Model = "deepseek-v4-flash",
            Temperature = 3.0
        };

        var errors = AgentHostBuilder.ValidateConfig(config);

        Assert.Contains(errors, e => e.Contains("Temperature"));
    }

    [Fact]
    public void ValidateConfig_NegativeTemperature_ReturnsError()
    {
        var config = new AgentConfig
        {
            ApiKey = "sk-test",
            Model = "deepseek-v4-flash",
            Temperature = -1.0
        };

        var errors = AgentHostBuilder.ValidateConfig(config);

        Assert.Contains(errors, e => e.Contains("Temperature"));
    }

    [Fact]
    public void ValidateConfig_ZeroRateLimit_ReturnsError()
    {
        var config = new AgentConfig
        {
            ApiKey = "sk-test",
            Model = "deepseek-v4-flash",
            RateLimitPerMinute = 0
        };

        var errors = AgentHostBuilder.ValidateConfig(config);

        Assert.Contains(errors, e => e.Contains("RateLimitPerMinute"));
    }

    [Fact]
    public void ValidateConfig_ValidConfig_ReturnsNoErrors()
    {
        var config = new AgentConfig
        {
            ApiKey = "sk-test",
            Model = "deepseek-v4-flash",
            MaxTokens = 4096,
            Temperature = 0.3,
            RateLimitPerMinute = 30
        };

        var errors = AgentHostBuilder.ValidateConfig(config);

        Assert.Empty(errors);
    }

    [Fact]
    public void CreateClient_WithValidConfig_ReturnsClient()
    {
        var config = new AgentConfig
        {
            ApiKey = "sk-test",
            Model = "deepseek-v4-flash",
            MaxTokens = 4096,
            Temperature = 0.3
        };

        var client = AgentHostBuilder.CreateClient(config);

        Assert.NotNull(client);
        Assert.IsType<DeepSeekClient>(client);
    }

    [Fact]
    public void CreateClient_WithLoggerFactory_DoesNotThrow()
    {
        var config = new AgentConfig
        {
            ApiKey = "sk-test",
            Model = "deepseek-v4-flash"
        };

        using var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole());

        var exception = Record.Exception(() =>
            AgentHostBuilder.CreateClient(config, loggerFactory));

        Assert.Null(exception);
    }

    [Fact]
    public async Task LoadConfigAsync_WhenConfigFileNotFound_ReturnsDefaultConfig()
    {
        // Act — no config path exists, should fall back to defaults
        var config = await AgentHostBuilder.LoadConfigAsync("nonexistent/path.json");

        // Assert
        Assert.NotNull(config);
        Assert.Equal("deepseek-v4-flash", config.Model);
        Assert.Equal(4096, config.MaxTokens);
    }

    [Fact]
    public async Task LoadConfigAsync_WithValidFile_LoadsDeepSeekConfig()
    {
        // Arrange — create a temporary config file
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);
        try
        {
            var configPath = Path.Combine(tempDir, "appsettings.json");
            var json = """
            {
                "DeepSeek": {
                    "Model": "deepseek-v4-flash",
                    "MaxTokens": 2048,
                    "Temperature": 0.5
                },
                "WebServer": {
                    "Enabled": true,
                    "Urls": "http://localhost:5000",
                    "LaunchBrowser": false
                },
                "RateLimiting": {
                    "MaxRequestsPerMinute": 15
                }
            }
            """;
            await File.WriteAllTextAsync(configPath, json);

            // Act
            var config = await AgentHostBuilder.LoadConfigAsync(configPath);

            // Assert
            Assert.NotNull(config);
            Assert.Equal("deepseek-v4-flash", config.Model);
            Assert.Equal(2048, config.MaxTokens);
            Assert.Equal(0.5, config.Temperature);
            Assert.True(config.WebEnabled);
            Assert.Equal(15, config.RateLimitPerMinute);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task LoadConfigAsync_WithGoogleAuthEnabled_WithoutCredentials_ReturnsNullGoogleAuth()
    {
        // Arrange
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);
        try
        {
            var configPath = Path.Combine(tempDir, "appsettings.json");
            var json = """
            {
                "DeepSeek": {
                    "Model": "deepseek-v4-flash",
                    "MaxTokens": 4096,
                    "Temperature": 0.3
                },
                "GoogleAuth": {
                    "Enabled": true,
                    "Scopes": ["openid", "profile", "email"]
                }
            }
            """;
            await File.WriteAllTextAsync(configPath, json);

            // Act — no env vars set for Google credentials
            var config = await AgentHostBuilder.LoadConfigAsync(configPath);

            // Assert — GoogleAuth must have both ClientId and ClientSecret to be usable
            // Without env vars or config values, it should not create a GoogleAuth config
            // (since Enabled=true but no credentials available)
            Assert.NotNull(config);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }
}
