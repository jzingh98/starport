using System.Security.Claims;
using System.Text.Json;

namespace Starport.Api.Auth;

public static class OperatorAuthorization
{
    public static bool CanModerate(ClaimsPrincipal user, IConfiguration config)
    {
        var configured = config.GetSection("Operator:Subjects").Get<string[]>() ?? [];
        if (configured.Contains(user.Subject(), StringComparer.Ordinal))
            return true;
        foreach (var claim in user.FindAll("resource_access"))
        {
            try
            {
                using var document = JsonDocument.Parse(claim.Value);
                if (document.RootElement.ValueKind == JsonValueKind.Object &&
                    document.RootElement.TryGetProperty("starport", out var client) &&
                    client.ValueKind == JsonValueKind.Object &&
                    client.TryGetProperty("roles", out var roles) && roles.ValueKind == JsonValueKind.Array &&
                    roles.EnumerateArray().Any(r => r.ValueKind == JsonValueKind.String && r.GetString() == "moderator"))
                    return true;
            }
            catch (JsonException) { }
        }
        return false;
    }
}
