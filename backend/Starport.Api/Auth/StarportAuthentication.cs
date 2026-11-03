using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;

namespace Starport.Api.Auth;

public static class StarportAuthentication
{
    public static void AddStarportAuthentication(this WebApplicationBuilder builder)
    {
        var mode = builder.Configuration["Auth:Mode"]?.Trim().ToLowerInvariant();
        switch (mode)
        {
            case "development" when builder.Environment.IsDevelopment():
                if (string.IsNullOrWhiteSpace(builder.Configuration["Auth:Development:Subject"]))
                    throw new InvalidOperationException("Auth:Development:Subject is required.");
                builder.Services.AddAuthentication(DevelopmentAuthenticationHandler.SchemeName)
                    .AddScheme<AuthenticationSchemeOptions, DevelopmentAuthenticationHandler>(
                        DevelopmentAuthenticationHandler.SchemeName, _ => { });
                break;
            case "oidc":
                var authority = builder.Configuration["Auth:Oidc:Authority"];
                var audience = builder.Configuration["Auth:Oidc:Audience"];
                var metadataAddress = builder.Configuration["Auth:Oidc:MetadataAddress"];
                if (!Uri.TryCreate(authority, UriKind.Absolute, out var parsed) ||
                    !AllowedOidcUrl(parsed, builder.Environment) || string.IsNullOrWhiteSpace(audience) ||
                    string.IsNullOrWhiteSpace(builder.Configuration["Auth:Oidc:ClientId"]))
                    throw new InvalidOperationException("Auth:Oidc:Authority, Audience and ClientId are required; the authority must be HTTPS outside local development.");
                if (!string.IsNullOrWhiteSpace(metadataAddress) &&
                    (!Uri.TryCreate(metadataAddress, UriKind.Absolute, out var metadataUri) ||
                     !AllowedOidcUrl(metadataUri, builder.Environment)))
                    throw new InvalidOperationException("Auth:Oidc:MetadataAddress must be an allowed absolute URL.");
                builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
                    .AddJwtBearer(options =>
                    {
                        options.Authority = authority;
                        options.Audience = audience;
                        options.RequireHttpsMetadata = parsed.Scheme == Uri.UriSchemeHttps;
                        if (!string.IsNullOrWhiteSpace(metadataAddress))
                            options.MetadataAddress = metadataAddress;
                        var issuer = authority.TrimEnd('/');
                        options.TokenValidationParameters.ValidIssuers = [issuer, issuer + "/"];
                        options.MapInboundClaims = false;
                        options.TokenValidationParameters.NameClaimType = "name";
                        options.TokenValidationParameters.RoleClaimType = "role";
                    });
                break;
            default:
                throw new InvalidOperationException("Auth:Mode must be 'oidc', or 'development' in Development only.");
        }
        builder.Services.AddAuthorization();
    }

    public static string? Subject(this ClaimsPrincipal user) =>
        user.Identity?.IsAuthenticated == true ? user.FindFirstValue("sub") : null;

    private static bool AllowedOidcUrl(Uri uri, IHostEnvironment environment) =>
        uri.Scheme == Uri.UriSchemeHttps ||
        environment.IsDevelopment() && uri.Scheme == Uri.UriSchemeHttp &&
        (uri.Host is "localhost" or "127.0.0.1" or "keycloak");
}

/// <summary>A single configured identity for local development. Never registered outside Development.</summary>
public sealed class DevelopmentAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IConfiguration configuration)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "StarportDevelopment";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var subject = configuration["Auth:Development:Subject"]!;
        var identity = new ClaimsIdentity([new Claim("sub", subject)], SchemeName);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(
            new ClaimsPrincipal(identity), SchemeName)));
    }
}
