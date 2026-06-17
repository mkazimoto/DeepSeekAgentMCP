using System.Collections.Concurrent;

namespace DeepSeekAgentMCP;

/// <summary>
/// sliding window rate limiter thread-safe para controle de requisições por IP/session.
/// </summary>
public class RateLimiter
{
    private readonly ConcurrentDictionary<string, SlidingWindow> _windows = new(StringComparer.Ordinal);
    private readonly int _maxRequests;
    private readonly TimeSpan _windowSize;
    private readonly Timer _cleanupTimer;

    /// <param name="maxRequests">Número máximo de requisições permitidas na janela.</param>
    /// <param name="windowSize">Tamanho da janela de tempo.</param>
    /// <param name="cleanupInterval">Intervalo para limpeza de entradas expiradas (default 5 min).</param>
    public RateLimiter(int maxRequests, TimeSpan windowSize, TimeSpan? cleanupInterval = null)
    {
        _maxRequests = maxRequests;
        _windowSize = windowSize;
        _cleanupTimer = new Timer(_ => CleanupExpired(), null, cleanupInterval ?? TimeSpan.FromMinutes(5), cleanupInterval ?? TimeSpan.FromMinutes(5));
    }

    /// <summary>
    /// Tenta consumir uma permissão para a chave informada.
    /// </summary>
    /// <returns>True se a requisição é permitida; False se excedeu o limite.</returns>
    public bool TryConsume(string key)
    {
        var window = _windows.GetOrAdd(key, _ => new SlidingWindow(_maxRequests, _windowSize));
        return window.TryConsume();
    }

    /// <summary>
    /// Retorna quantas requisições ainda podem ser feitas na janela atual.
    /// </summary>
    public int GetRemainingRequests(string key)
    {
        if (_windows.TryGetValue(key, out var window))
            return window.GetRemaining();
        return _maxRequests;
    }

    /// <summary>
    /// Remove manualmente uma chave do rate limiter.
    /// </summary>
    public void Reset(string key)
    {
        _windows.TryRemove(key, out _);
    }

    private void CleanupExpired()
    {
        var now = DateTime.UtcNow;
        foreach (var kvp in _windows)
        {
            if (kvp.Value.IsExpired(now))
            {
                _windows.TryRemove(kvp.Key, out _);
            }
        }
    }

    public void Dispose()
    {
        _cleanupTimer.Dispose();
        _windows.Clear();
    }

    /// <summary>
    /// Implementação de sliding window usando fila de timestamps.
    /// </summary>
    private class SlidingWindow
    {
        private readonly int _maxRequests;
        private readonly TimeSpan _windowSize;
        private readonly ConcurrentQueue<DateTime> _timestamps = [];
        private readonly object _lock = new();

        public SlidingWindow(int maxRequests, TimeSpan windowSize)
        {
            _maxRequests = maxRequests;
            _windowSize = windowSize;
        }

        public bool TryConsume()
        {
            var now = DateTime.UtcNow;
            lock (_lock)
            {
                // Remove timestamps fora da janela
                while (_timestamps.TryPeek(out var oldest) && now - oldest > _windowSize)
                {
                    _timestamps.TryDequeue(out _);
                }

                if (_timestamps.Count >= _maxRequests)
                    return false;

                _timestamps.Enqueue(now);
                return true;
            }
        }

        public int GetRemaining()
        {
            var now = DateTime.UtcNow;
            lock (_lock)
            {
                while (_timestamps.TryPeek(out var oldest) && now - oldest > _windowSize)
                {
                    _timestamps.TryDequeue(out _);
                }
                return _maxRequests - _timestamps.Count;
            }
        }

        public bool IsExpired(DateTime now)
        {
            lock (_lock)
            {
                if (_timestamps.TryPeek(out var oldest))
                    return now - oldest > _windowSize * 2;
                return true; // Empty = expired
            }
        }
    }
}
