using StackExchange.Redis;

namespace WebApi.Services;

public interface IRateLimitService
{
    Task<bool> TryConsumeAsync(long userId);
}

public sealed class RateLimitService : IRateLimitService
{
    private const int MaxRequestsPerMinute = 60;
    private const string IncrementScript = "local count = redis.call('INCR', KEYS[1]); if count == 1 then redis.call('EXPIRE', KEYS[1], ARGV[1]); end; return count";
    private readonly IDatabase _database;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<RateLimitService> _logger;

    public RateLimitService(
        IConnectionMultiplexer connection,
        TimeProvider timeProvider,
        ILogger<RateLimitService> logger)
    {
        _database = connection.GetDatabase();
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task<bool> TryConsumeAsync(long userId)
    {
        var currentUnixSecond = _timeProvider.GetUtcNow().ToUnixTimeSeconds();
        var currentWindow = currentUnixSecond / 60;
        var secondsUntilWindowEnd = 60 - currentUnixSecond % 60;
        var key = new RedisKey($"fitnessclub:rate-limit:{userId}:{currentWindow}");

        try
        {
            var result = await _database.ScriptEvaluateAsync(
                IncrementScript,
                new[] { key },
                new RedisValue[] { secondsUntilWindowEnd });

            return (long)result <= MaxRequestsPerMinute;
        }
        catch (Exception exception) when (exception is RedisException or TimeoutException or ObjectDisposedException)
        {
            // решение: rate limit работает fail-closed, чтобы отказ Redis не отключал ограничение запросов.
            _logger.LogError(exception, "Redis rate limiter unavailable; rejecting request for user {UserId}.", userId);
            return false;
        }
    }
}
