using Common.Caching;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using StackExchange.Redis;
using WebApi.Services;

namespace WebApi.Tests;

[TestClass]
public class RedisFallbackTests
{
    [TestMethod]
    public async Task CacheRead_WhenRedisIsUnavailable_ReturnsCacheMiss()
    {
        var database = new Mock<IDatabase>();
        database
            .Setup(x => x.StringGetAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .Returns(Task.FromException<RedisValue>(RedisUnavailable()));
        var cache = CreateCache(database.Object);

        var result = await cache.GetAsync<string>("cache-key");

        Assert.IsNull(result);
    }

    [TestMethod]
    public async Task CacheWriteAndRemove_WhenRedisIsUnavailable_DoNotThrow()
    {
        var database = new Mock<IDatabase>();
        database
            .Setup(x => x.StringSetAsync(
                It.IsAny<RedisKey>(),
                It.IsAny<RedisValue>(),
                It.IsAny<TimeSpan?>(),
                It.IsAny<bool>(),
                It.IsAny<When>(),
                It.IsAny<CommandFlags>()))
            .Returns(Task.FromException<bool>(RedisUnavailable()));
        database
            .Setup(x => x.KeyDeleteAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .Returns(Task.FromException<bool>(RedisUnavailable()));
        var cache = CreateCache(database.Object);

        await cache.SetAsync("cache-key", "value");
        await cache.RemoveAsync("cache-key");
    }

    [TestMethod]
    public async Task RateLimiter_WhenRedisIsUnavailable_RejectsRequest()
    {
        var database = new Mock<IDatabase>();
        database
            .Setup(x => x.ScriptEvaluateAsync(
                It.IsAny<string>(),
                It.IsAny<RedisKey[]>(),
                It.IsAny<RedisValue[]>(),
                It.IsAny<CommandFlags>()))
            .Returns(Task.FromException<RedisResult>(RedisUnavailable()));
        var connection = new Mock<IConnectionMultiplexer>();
        connection
            .Setup(x => x.GetDatabase(It.IsAny<int>(), It.IsAny<object>()))
            .Returns(database.Object);
        var rateLimiter = new RateLimitService(
            connection.Object,
            TimeProvider.System,
            NullLogger<RateLimitService>.Instance);

        var allowed = await rateLimiter.TryConsumeAsync(42);

        Assert.IsFalse(allowed);
    }

    private static RedisCacheService CreateCache(IDatabase database)
    {
        var connection = new Mock<IConnectionMultiplexer>();
        connection
            .Setup(x => x.GetDatabase(It.IsAny<int>(), It.IsAny<object>()))
            .Returns(database);

        return new RedisCacheService(
            connection.Object,
            TimeSpan.FromMinutes(5),
            NullLogger<RedisCacheService>.Instance);
    }

    private static RedisConnectionException RedisUnavailable()
    {
        return new RedisConnectionException(ConnectionFailureType.UnableToConnect, "Redis unavailable in test");
    }
}
