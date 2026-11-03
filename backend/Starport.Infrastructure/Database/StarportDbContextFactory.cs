using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Starport.Infrastructure.Database;

/// <summary>Design-time model construction. Commands that only inspect the model need no running database.</summary>
public sealed class StarportDbContextFactory : IDesignTimeDbContextFactory<StarportDbContext>
{
    public StarportDbContext CreateDbContext(string[] args)
    {
        var connection = Environment.GetEnvironmentVariable("ConnectionStrings__Database")
            ?? "Host=localhost;Port=5436;Database=starport;Username=starport;Password=starport";
        var options = new DbContextOptionsBuilder<StarportDbContext>()
            .UseNpgsql(connection, npgsql => npgsql.MigrationsAssembly(typeof(StarportDbContext).Assembly.FullName))
            .Options;
        return new StarportDbContext(options);
    }
}
