using DeepSeekAgentMCP;

namespace DeepSeekAgentMCP.Tests;

public class SessionManagerTests
{
    private static SessionManager CreateManager()
    {
        return new SessionManager(new FakeDeepSeekClient(), new FakeMcpToolManager(), userLogger: null);
    }

    [Fact]
    public async Task ActiveSessionCount_StartsAtZero()
    {
        var mgr = CreateManager();
        Assert.Equal(0, mgr.ActiveSessionCount);
    }

    [Fact]
    public async Task ProcessMessageAsync_CreatesSessionOnFirstMessage()
    {
        var mgr = CreateManager();
        await mgr.ProcessMessageAsync("session-1", "Hello");
        Assert.Equal(1, mgr.ActiveSessionCount);
    }

    [Fact]
    public async Task ProcessMessageAsync_TwoSessions_AreIndependent()
    {
        var mgr = CreateManager();
        await mgr.ProcessMessageAsync("sess-a", "Hello A");
        await mgr.ProcessMessageAsync("sess-b", "Hello B");
        Assert.Equal(2, mgr.ActiveSessionCount);
    }

    [Fact]
    public async Task ProcessMessageAsync_SameSession_ReusesAgent()
    {
        var mgr = CreateManager();
        await mgr.ProcessMessageAsync("session-1", "First");
        await mgr.ProcessMessageAsync("session-1", "Second");
        Assert.Equal(1, mgr.ActiveSessionCount);
    }

    [Fact]
    public async Task ClearConversationAsync_ClearsHistory()
    {
        var mgr = CreateManager();
        await mgr.ProcessMessageAsync("session-1", "Hello");
        await mgr.ClearConversationAsync("session-1");
        Assert.Empty(mgr.GetHistory("session-1"));
    }

    [Fact]
    public async Task CancelRequest_DoesNotThrow()
    {
        var mgr = CreateManager();
        mgr.CancelRequest("session-1");
        Assert.Equal(0, mgr.ActiveSessionCount);
    }

    [Fact]
    public async Task RemoveSession_ReturnsTrue_WhenExists()
    {
        var mgr = CreateManager();
        await mgr.ProcessMessageAsync("session-1", "Hi");
        Assert.True(mgr.RemoveSession("session-1"));
        Assert.Equal(0, mgr.ActiveSessionCount);
    }

    [Fact]
    public async Task RemoveSession_ReturnsFalse_WhenNotExists()
    {
        var mgr = CreateManager();
        Assert.False(mgr.RemoveSession("nonexistent"));
    }

    [Fact]
    public async Task GetHistory_ReturnsEmpty_WhenSessionNotExists()
    {
        var mgr = CreateManager();
        Assert.Empty(mgr.GetHistory("nonexistent"));
    }

    [Fact]
    public async Task ActiveSessionCount_IncreasesAndDecreases()
    {
        var mgr = CreateManager();
        Assert.Equal(0, mgr.ActiveSessionCount);
        await mgr.ProcessMessageAsync("s1", "Hi");
        Assert.Equal(1, mgr.ActiveSessionCount);
        await mgr.ProcessMessageAsync("s2", "Hi");
        Assert.Equal(2, mgr.ActiveSessionCount);
        mgr.RemoveSession("s1");
        Assert.Equal(1, mgr.ActiveSessionCount);
    }

    [Fact]
    public async Task DisposeAsync_DoesNotThrow()
    {
        var mgr = CreateManager();
        await mgr.ProcessMessageAsync("s1", "Hi");
        await mgr.DisposeAsync();
    }

    [Fact]
    public async Task GetSessionCountForIp_ReturnsCorrectCount()
    {
        var mgr = CreateManager();
        await mgr.ProcessMessageAsync("s1", "Hi", "192.168.1.1");
        await mgr.ProcessMessageAsync("s2", "Hi", "192.168.1.1");
        await mgr.ProcessMessageAsync("s3", "Hi", "10.0.0.1");
        Assert.Equal(2, mgr.GetSessionCountForIp("192.168.1.1"));
        Assert.Equal(1, mgr.GetSessionCountForIp("10.0.0.1"));
        Assert.Equal(0, mgr.GetSessionCountForIp("unknown"));
    }

    [Fact]
    public async Task SessionExists_ReturnsCorrectResult()
    {
        var mgr = CreateManager();
        await mgr.ProcessMessageAsync("existing", "Hi");
        Assert.True(mgr.SessionExists("existing"));
        Assert.False(mgr.SessionExists("nonexistent"));
    }
}
