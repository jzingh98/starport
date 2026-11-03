using Npgsql;

namespace Starport.Api.Startup;

public static class DatabaseConnection
{
    public static string Require(IConfiguration config, IHostEnvironment environment)
    {
        if (environment.IsDevelopment() && config.GetConnectionString("Database") is { Length: > 0 } local)
            return local;

        var host = config["Database:Host"];
        var name = config["Database:Name"];
        var username = config["Database:Username"];
        var password = config["Database:Password"];
        var sslSetting = config["Database:SslMode"] ??
            (environment.IsDevelopment() ? "Prefer" : "Require");
        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(name) ||
            string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password) ||
            !int.TryParse(config["Database:MaxPoolSize"], out var maxPool) || maxPool is < 1 or > 100 ||
            !int.TryParse(config["Database:Port"] ?? "5432", out var port) || port is < 1 or > 65535 ||
            !Enum.TryParse<SslMode>(sslSetting, true, out var sslMode) || !Enum.IsDefined(sslMode) ||
            (!environment.IsDevelopment() && sslMode is SslMode.Disable or SslMode.Allow or SslMode.Prefer))
            throw new InvalidOperationException("Database:Host/Name/Username/Password/MaxPoolSize, a valid Port, and a secure SslMode outside Development are required.");

        return new NpgsqlConnectionStringBuilder
        {
            Host = host,
            Port = port,
            Database = name,
            Username = username,
            Password = password,
            MaxPoolSize = maxPool,
            SslMode = sslMode,
            Pooling = true
        }.ConnectionString;
    }
}
