namespace Starport.Api.Delivery;

public sealed class GameDeliverySettings
{
    public bool Enabled { get; }
    public string RootDomain { get; }
    public string Scheme { get; }
    public int? Port { get; }
    public string AppOrigin { get; }

    public GameDeliverySettings(IConfiguration config, IHostEnvironment environment)
    {
        Enabled = config.GetValue<bool>("GameDelivery:Enabled");
        RootDomain = (config["GameDelivery:RootDomain"] ?? "").Trim().ToLowerInvariant();
        Scheme = config["GameDelivery:Scheme"] ?? "https";
        AppOrigin = (config["GameDelivery:AppOrigin"] ?? "").TrimEnd('/');
        if (int.TryParse(config["GameDelivery:Port"], out var port)) Port = port;
        if (!Enabled) return;
        if (string.IsNullOrWhiteSpace(RootDomain) || RootDomain.Contains('/') ||
            !Uri.TryCreate(AppOrigin, UriKind.Absolute, out var origin) ||
            (Scheme != "https" && !(environment.IsDevelopment() && Scheme == "http")) ||
            (origin.Scheme != "https" && !(environment.IsDevelopment() && origin.Scheme == "http")) ||
            origin.GetLeftPart(UriPartial.Authority) != AppOrigin)
            throw new InvalidOperationException("GameDelivery root domain, app origin and HTTPS scheme are required.");
    }

    public bool TryGameHost(string host, out Guid gameId)
    {
        gameId = Guid.Empty;
        if (!Enabled) return false;
        var suffix = "." + RootDomain;
        if (!host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return false;
        var label = host[..^suffix.Length];
        return label.StartsWith("g-", StringComparison.OrdinalIgnoreCase) &&
            Guid.TryParseExact(label[2..], "N", out gameId);
    }

    public string Origin(Guid gameId) => Enabled
        ? $"{Scheme}://g-{gameId:N}.{RootDomain}" + (Port is { } port ? $":{port}" : "")
        : throw new InvalidOperationException("Game delivery is disabled.");
}
