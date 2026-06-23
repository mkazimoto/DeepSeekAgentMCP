using System.Text.Json;
using DeepSeekAgentMCP.Models;

namespace DeepSeekAgentMCP.Tests;

public class UserLoggerTests : IDisposable
{
    private readonly string _testLogDir;

    public UserLoggerTests()
    {
        _testLogDir = Path.Combine(Path.GetTempPath(), "DeepSeekAgentMCP_Tests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_testLogDir);
    }

    [Fact]
    public void Log_AddsEntryToBuffer()
    {
        using var logger = new UserLogger(maxBufferEntries: 100);
        logger.LogEvent("test_event", user: "Test User", email: "test@example.com", sessionId: "sess-1", clientIp: "127.0.0.1", detail: "Test detail");

        Assert.Equal(1, logger.BufferedCount);
    }

    [Fact]
    public void GetRecentLogs_ReturnsNewestFirst()
    {
        using var logger = new UserLogger(maxBufferEntries: 100);
        logger.LogEvent("event_a", user: "User1");
        logger.LogEvent("event_b", user: "User2");
        logger.LogEvent("event_c", user: "User3");

        var recent = logger.GetRecentLogs(2);
        Assert.Equal(2, recent.Count);
        Assert.Equal("event_c", recent[0].Event);
        Assert.Equal("event_b", recent[1].Event);
    }

    [Fact]
    public void GetAllLogs_ReturnsAllEntries()
    {
        using var logger = new UserLogger(maxBufferEntries: 100);
        logger.LogEvent("e1");
        logger.LogEvent("e2");
        logger.LogEvent("e3");

        var all = logger.GetAllLogs();
        Assert.Equal(3, all.Count);
    }

    [Fact]
    public void BufferRespectsMaxEntries()
    {
        using var logger = new UserLogger(maxBufferEntries: 5);
        for (var i = 0; i < 10; i++)
            logger.LogEvent($"event_{i}");

        Assert.Equal(5, logger.BufferedCount);
    }

    [Fact]
    public void LogWithAllFields_StoresCorrectly()
    {
        using var logger = new UserLogger(maxBufferEntries: 100);
        var now = DateTime.UtcNow;

        logger.Log(new UserLogEntry
        {
            Event = "login",
            User = "John Doe",
            Email = "john@example.com",
            SessionId = "abc-123",
            ClientIp = "192.168.1.1",
            Detail = "Google OAuth login",
            Timestamp = now
        });

        var entries = logger.GetAllLogs();
        var entry = Assert.Single(entries);

        Assert.Equal("login", entry.Event);
        Assert.Equal("John Doe", entry.User);
        Assert.Equal("john@example.com", entry.Email);
        Assert.Equal("abc-123", entry.SessionId);
        Assert.Equal("192.168.1.1", entry.ClientIp);
        Assert.Equal("Google OAuth login", entry.Detail);
        Assert.Equal(now.Ticks / TimeSpan.TicksPerSecond, entry.Timestamp.Ticks / TimeSpan.TicksPerSecond);
    }

    [Fact]
    public void Flush_WritesToDailyFile()
    {
        using var logger = new UserLogger(_testLogDir, maxBufferEntries: 100, flushIntervalSeconds: 60);
        logger.LogEvent("test_flush", user: "Flush User", detail: "Testing daily file");
        logger.Flush();

        var todayFile = Path.Combine(_testLogDir, $"user-log-{DateTime.UtcNow:yyyy-MM-dd}.json");
        Assert.True(File.Exists(todayFile));
        var lines = File.ReadAllLines(todayFile);
        Assert.Single(lines);
        Assert.Contains("test_flush", lines[0]);
        Assert.Contains("Flush User", lines[0]);
    }

    [Fact]
    public void Flush_MultipleEntries_AllWritten()
    {
        using var logger = new UserLogger(_testLogDir, maxBufferEntries: 100, flushIntervalSeconds: 60);
        logger.LogEvent("e1");
        logger.LogEvent("e2");
        logger.LogEvent("e3");
        logger.Flush();

        var todayFile = Path.Combine(_testLogDir, $"user-log-{DateTime.UtcNow:yyyy-MM-dd}.json");
        var lines = File.ReadAllLines(todayFile);
        Assert.Equal(3, lines.Length);
    }

    [Fact]
    public void ReadFromDisk_ReturnsEntriesFromToday()
    {
        using var logger = new UserLogger(_testLogDir, maxBufferEntries: 100, flushIntervalSeconds: 60);
        logger.LogEvent("disk_entry", user: "Disk User", detail: "Today's entry");
        logger.Flush();

        var entries = logger.ReadFromDisk(maxLines: 10);
        Assert.NotEmpty(entries);
        Assert.Contains(entries, e => e.Detail == "Today's entry");
    }

    [Fact]
    public void ReadFromDisk_RespectsMaxLines()
    {
        using var logger = new UserLogger(_testLogDir, maxBufferEntries: 100, flushIntervalSeconds: 60);
        for (var i = 0; i < 20; i++)
            logger.LogEvent($"disk_{i}");
        logger.Flush();

        var entries = logger.ReadFromDisk(maxLines: 5);
        Assert.Equal(5, entries.Count);
    }

    [Fact]
    public void ReadFromDisk_NoDirectory_ReturnsEmpty()
    {
        using var logger = new UserLogger(maxBufferEntries: 100);
        var entries = logger.ReadFromDisk();
        Assert.Empty(entries);
    }

    [Fact]
    public void GetDailyFiles_ReturnsFilesSorted()
    {
        using var logger = new UserLogger(_testLogDir, maxBufferEntries: 100, flushIntervalSeconds: 60);
        logger.LogEvent("file_test");
        logger.Flush();

        var files = logger.GetDailyFiles();
        Assert.NotEmpty(files);
        Assert.All(files, f => Assert.EndsWith(".json", f));
    }

    [Fact]
    public void UserLogEntry_SerializesAndDeserializes()
    {
        var entry = new UserLogEntry
        {
            Event = "login",
            User = "Test User",
            Email = "test@example.com",
            SessionId = "sess-1",
            ClientIp = "10.0.0.1",
            Detail = "Test",
            Timestamp = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        };

        var json = JsonSerializer.Serialize(entry);
        var deserialized = JsonSerializer.Deserialize<UserLogEntry>(json);

        Assert.NotNull(deserialized);
        Assert.Equal(entry.Event, deserialized.Event);
        Assert.Equal(entry.User, deserialized.User);
        Assert.Equal(entry.Email, deserialized.Email);
        Assert.Equal(entry.SessionId, deserialized.SessionId);
        Assert.Equal(entry.ClientIp, deserialized.ClientIp);
        Assert.Equal(entry.Detail, deserialized.Detail);
    }

    [Fact]
    public void Dispose_FlushesRemainingEntries()
    {
        var logger = new UserLogger(_testLogDir, maxBufferEntries: 100, flushIntervalSeconds: 60);
        logger.LogEvent("dispose_test");
        logger.Dispose();

        var todayFile = Path.Combine(_testLogDir, $"user-log-{DateTime.UtcNow:yyyy-MM-dd}.json");
        Assert.True(File.Exists(todayFile));
        var lines = File.ReadAllLines(todayFile);
        Assert.Contains(lines, l => l.Contains("dispose_test"));
    }

    [Fact]
    public void LogAfterDispose_DoesNotThrow()
    {
        var logger = new UserLogger(maxBufferEntries: 100);
        logger.Dispose();

        logger.LogEvent("after_dispose");
    }

    [Fact]
    public void NoLogDirectory_DoesNotWriteToDisk()
    {
        using var logger = new UserLogger(maxBufferEntries: 100);
        logger.LogEvent("no_disk");
        logger.Flush();

        var entries = logger.ReadFromDisk();
        Assert.Empty(entries);
    }

    [Fact]
    public void DailyFileName_MatchesExpectedFormat()
    {
        var date = new DateTime(2026, 6, 23, 12, 0, 0, DateTimeKind.Utc);
        using var logger = new UserLogger(_testLogDir, maxBufferEntries: 100, flushIntervalSeconds: 60);

        var method = typeof(UserLogger).GetMethod("GetDailyLogFilePath",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);

        var result = method.Invoke(null, [_testLogDir, date]) as string;
        Assert.NotNull(result);
        Assert.EndsWith("user-log-2026-06-23.json", result);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testLogDir))
                Directory.Delete(_testLogDir, recursive: true);
        }
        catch
        {
            // Ignore cleanup errors
        }
    }
}
