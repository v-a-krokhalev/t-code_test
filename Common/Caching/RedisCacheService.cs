using System.Text.Json;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Common.Caching;

public class RedisCacheService : ICacheService
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly IConnectionMultiplexer _connection;
    private readonly TimeSpan _defaultTtl;
    private readonly ILogger<RedisCacheService> _logger;

    public RedisCacheService(
        IConnectionMultiplexer connection,
        TimeSpan defaultTtl,
        ILogger<RedisCacheService> logger)
    {
        _connection = connection;
        _defaultTtl = defaultTtl;
        _logger = logger;
    }

    public async Task<T?> GetAsync<T>(string key, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            var value = await _connection.GetDatabase().StringGetAsync(key);

            if (value.IsNullOrEmpty)
            {
                return default;
            }

            return JsonSerializer.Deserialize<T>(value!, SerializerOptions);
        }
        catch (Exception exception) when (IsCacheUnavailable(exception) || exception is JsonException)
        {
            // решение: кеш работает fail-open, поскольку Postgres остаётся источником истины для этих данных.
            _logger.LogWarning(exception, "Redis cache read failed for key {CacheKey}; treating it as a cache miss.", key);
            return default;
        }
    }

    public async Task SetAsync<T>(string key, T value, TimeSpan? ttl = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var payload = JsonSerializer.Serialize(value, SerializerOptions);

        try
        {
            await _connection.GetDatabase().StringSetAsync(key, payload, ttl ?? _defaultTtl);
        }
        catch (Exception exception) when (IsCacheUnavailable(exception))
        {
            // решение: сбой записи в кеш не должен превращать успешное чтение или изменение данных в ошибку API.
            _logger.LogWarning(exception, "Redis cache write failed for key {CacheKey}; continuing without caching.", key);
        }
    }

    public async Task RemoveAsync(string key, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            await _connection.GetDatabase().KeyDeleteAsync(key);
        }
        catch (Exception exception) when (IsCacheUnavailable(exception))
        {
            _logger.LogWarning(exception, "Redis cache removal failed for key {CacheKey}.", key);
        }
    }

    private static bool IsCacheUnavailable(Exception exception)
    {
        return exception is RedisException or TimeoutException or ObjectDisposedException;
    }
}
