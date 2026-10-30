using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace DataAccessLayer;

public sealed class ClubDbContextFactory : IDesignTimeDbContextFactory<ClubDbContext>
{
    public ClubDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("POSTGRES_DESIGN_TIME")
            ?? "Host=localhost;Port=5433;Database=club;Username=club;Password=club";
        var options = new DbContextOptionsBuilder<ClubDbContext>()
            .UseNpgsql(connectionString)
            .Options;

        return new ClubDbContext(options);
    }
}