using System.Collections.Concurrent;
using System.Text.Json;
using DeepSeekAgentMCP.Models;

namespace DeepSeekAgentMCP;

/// <summary>
/// Thread-safe service that logs user activities (login, logout, messages, sessions, errors)
/// to daily rolling JSON log files and keeps an in-memory ring buffer for recent queries.
/// Each day produces a separate file: user-log-YYYY-MM-DD.json
/// </summary>
public class UserLogger : IDisposable
{
    private readonly string? _logDirectory;
    private readonly int _maxBufferEntries;
    private readonly ConcurrentQueue<UserLogEntry> _buffer;
    private readonly ReaderWriterLockSlim _fileLock = new();
    private readonly Timer? _flushTimer;
    private bool _disposed;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>
    /// Creates a UserLogger.
    /// If <paramref name="logDirectory"/> is null or empty, file logging is disabled
    /// (only the in-memory buffer works).
    /// </summary>
    /// <param name="logDirectory">Directory where daily log files will be written.</param>
    /// <param name="maxBufferEntries">Maximum in-memory buffer entries.</param>
    /// <param name="flushIntervalSeconds">How often to flush buffer to disk.</param>
    public UserLogger(string? logDirectory = null, int maxBufferEntries = 1000, int flushIntervalSeconds = 10)
    {
        _maxBufferEntries = maxBufferEntries;
        _buffer = new ConcurrentQueue<UserLogEntry>();

        if (!string.IsNullOrWhiteSpace(logDirectory))
        {
            _logDirectory = Path.GetFullPath(logDirectory);
            Directory.CreateDirectory(_logDirectory);

            _flushTimer = new Timer(_ => FlushToDisk(), null, TimeSpan.FromSeconds(flushIntervalSeconds), TimeSpan.FromSeconds(flushIntervalSeconds));
        }
    }

    /// <summary>
    /// Returns the path to a daily log file for the given date.
    /// </summary>
    private static string GetDailyLogFilePath(string directory, DateTime date)
    {
        var fileName = $"user-log-{date:yyyy-MM-dd}.json";
        return Path.Combine(directory, fileName);
    }

    /// <summary>
    /// Logs a user activity event.
    /// </summary>
    public void Log(UserLogEntry entry)
    {
        if (_disposed) return;

        _buffer.Enqueue(entry);

        // Trim buffer if it exceeds max size
        while (_buffer.Count > _maxBufferEntries && _buffer.TryDequeue(out _)) { }
    }

    /// <summary>
    /// Convenience method to log with individual parameters.
    /// </summary>
    public void LogEvent(string eventType, string? user = null, string? email = null, string? sessionId = null, string? clientIp = null, string? detail = null)
    {
        Log(new UserLogEntry
        {
            Event = eventType,
            User = user ?? "anonymous",
            Email = email,
            SessionId = sessionId,
            ClientIp = clientIp,
            Detail = detail
        });
    }

    /// <summary>
    /// Returns recent log entries from the in-memory buffer (newest first).
    /// </summary>
    public IReadOnlyList<UserLogEntry> GetRecentLogs(int count = 100)
    {
        return [.. _buffer.Reverse().Take(count)];
    }

    /// <summary>
    /// Returns all log entries from the in-memory buffer (oldest first).
    /// </summary>
    public IReadOnlyList<UserLogEntry> GetAllLogs()
    {
        return [.. _buffer];
    }

    /// <summary>
    /// Returns the total number of buffered entries.
    /// </summary>
    public int BufferedCount => _buffer.Count;

    /// <summary>
    /// Flushes buffered entries to disk immediately.
    /// </summary>
    public void Flush()
    {
        FlushToDisk();
    }

    private string? GetCurrentLogFilePath()
    {
        if (_logDirectory == null) return null;
        return GetDailyLogFilePath(_logDirectory, DateTime.UtcNow);
    }

    private void FlushToDisk()
    {
        if (_disposed || _logDirectory == null) return;

        // Drain the buffer into a local list
        var entries = new List<UserLogEntry>();
        while (_buffer.TryDequeue(out var entry))
        {
            entries.Add(entry);
        }

        if (entries.Count == 0) return;

        var dailyPath = GetCurrentLogFilePath();
        if (dailyPath == null) return;

        _fileLock.EnterWriteLock();
        try
        {
            var lines = entries.Select(e => JsonSerializer.Serialize(e, JsonOptions));
            File.AppendAllLines(dailyPath, lines);
        }
        catch (Exception ex)
        {
            // If writing fails, re-enqueue entries to avoid data loss
            foreach (var entry in entries)
                _buffer.Enqueue(entry);

            System.Console.Error.WriteLine($"[UserLogger] Failed to write to log file: {ex.Message}");
        }
        finally
        {
            _fileLock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Reads recent log entries from disk across daily files (newest first).
    /// Starts from today and goes back up to <paramref name="maxDays"/> days.
    /// </summary>
    public List<UserLogEntry> ReadFromDisk(int maxLines = 500, int maxDays = 7)
    {
        if (_logDirectory == null || !Directory.Exists(_logDirectory))
            return [];

        _fileLock.EnterReadLock();
        try
        {
            var entries = new List<UserLogEntry>();
            var today = DateTime.UtcNow;

            // Read from today backwards up to maxDays
            for (var i = 0; i < maxDays; i++)
            {
                var date = today.AddDays(-i);
                var filePath = GetDailyLogFilePath(_logDirectory, date);

                if (!File.Exists(filePath)) continue;

                var lines = File.ReadAllLines(filePath);

                foreach (var line in lines)
                {
                    try
                    {
                        var entry = JsonSerializer.Deserialize<UserLogEntry>(line, JsonOptions);
                        if (entry != null)
                            entries.Add(entry);
                    }
                    catch
                    {
                        // Skip malformed lines
                    }
                }
            }

            // Return newest first, limited to maxLines
            entries.Reverse();
            return entries.Take(maxLines).ToList();
        }
        finally
        {
            _fileLock.ExitReadLock();
        }
    }

    /// <summary>
    /// Lists all daily log files in the directory, newest first.
    /// </summary>
    public IReadOnlyList<string> GetDailyFiles()
    {
        if (_logDirectory == null || !Directory.Exists(_logDirectory))
            return [];

        return Directory.GetFiles(_logDirectory, "user-log-*.json")
            .OrderByDescending(f => f)
            .ToList();
    }

    public void Dispose()
    {
        if (_disposed) return;

        _flushTimer?.Dispose();
        // Flush before marking disposed so FlushToDisk() can still write
        FlushToDisk();
        _disposed = true;
        _fileLock.Dispose();
    }
}
