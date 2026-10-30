using Microsoft.EntityFrameworkCore;

namespace WebApi.Services;

public static class PostgresTransactionLocks
{
    public static Task AcquireAsync(DbContext dbContext, string key, CancellationToken ct)
    {
        return dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({key}, 0))",
            ct);
    }
}