namespace DeepSeekAgentMCP.Tests;

public class RateLimiterTests
{
    [Fact]
    public void TryConsume_ReturnsTrue_WhenUnderLimit()
    {
        var limiter = new RateLimiter(maxRequests: 5, windowSize: TimeSpan.FromMinutes(1));

        for (var i = 0; i < 5; i++)
        {
            Assert.True(limiter.TryConsume("test-key"));
        }
    }

    [Fact]
    public void TryConsume_ReturnsFalse_WhenOverLimit()
    {
        var limiter = new RateLimiter(maxRequests: 3, windowSize: TimeSpan.FromMinutes(1));

        Assert.True(limiter.TryConsume("test-key"));
        Assert.True(limiter.TryConsume("test-key"));
        Assert.True(limiter.TryConsume("test-key"));
        Assert.False(limiter.TryConsume("test-key"));
    }

    [Fact]
    public void TryConsume_DifferentKeys_AreIndependent()
    {
        var limiter = new RateLimiter(maxRequests: 2, windowSize: TimeSpan.FromMinutes(1));

        Assert.True(limiter.TryConsume("key-a"));
        Assert.True(limiter.TryConsume("key-a"));
        Assert.False(limiter.TryConsume("key-a"));

        Assert.True(limiter.TryConsume("key-b"));
        Assert.True(limiter.TryConsume("key-b"));
        Assert.False(limiter.TryConsume("key-b"));
    }

    [Fact]
    public void GetRemainingRequests_ReturnsCorrectCount()
    {
        var limiter = new RateLimiter(maxRequests: 5, windowSize: TimeSpan.FromMinutes(1));

        Assert.Equal(5, limiter.GetRemainingRequests("test-key"));

        limiter.TryConsume("test-key");
        Assert.Equal(4, limiter.GetRemainingRequests("test-key"));

        limiter.TryConsume("test-key");
        Assert.Equal(3, limiter.GetRemainingRequests("test-key"));
    }

    [Fact]
    public void GetRemainingRequests_UnknownKey_ReturnsMax()
    {
        var limiter = new RateLimiter(maxRequests: 10, windowSize: TimeSpan.FromMinutes(1));
        Assert.Equal(10, limiter.GetRemainingRequests("unknown-key"));
    }

    [Fact]
    public void Reset_ClearsKey()
    {
        var limiter = new RateLimiter(maxRequests: 2, windowSize: TimeSpan.FromMinutes(1));

        limiter.TryConsume("test-key");
        limiter.TryConsume("test-key");
        Assert.False(limiter.TryConsume("test-key"));

        limiter.Reset("test-key");
        Assert.True(limiter.TryConsume("test-key"));
    }
}
