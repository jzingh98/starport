using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Starport.Api.Auth;
using Starport.Api.Delivery;
using Starport.Api.Storage;
using Starport.Application.Access;
using Starport.Application.Publishing;
using Starport.Infrastructure.Database;

namespace Starport.Api.Endpoints;

public sealed record GameLaunchDto(string EmbedUrl, string ContentOrigin, string BridgeNonce,
    DateTimeOffset ExpiresAt, bool CanSave);
public sealed record RedeemLaunchBody(string? Ticket);

public static class GameDeliveryEndpoints
{
    private const string SessionCookie = "__Host-starport-game";
    private static readonly FileExtensionContentTypeProvider ContentTypes = new();

    public static void MapGameDeliveryEndpoints(this WebApplication app)
    {
        app.MapPost("/api/games/{gameId:guid}/launch", async (Guid gameId, HttpContext http,
            GameReadService access, StarportDbContext db, GameDeliverySettings delivery,
            DeliveryUsageMeter meter, TimeProvider clock, CancellationToken ct) =>
        {
            if (!delivery.Enabled)
                return ApiProblems.Problem(503, "delivery-unavailable", "Game delivery is unavailable");
            var subject = http.User.Subject();
            var preview = http.Request.Query["preview"] == "true";
            var read = await access.ReadAsync(gameId, subject, preview, ct);
            if (read.Game is null)
                return read.Decision == AccessDecision.DeliveryQuotaExceeded
                    ? ApiProblems.Problem(429, "delivery-quota-exceeded", "Game delivery allowance reached")
                    : ApiProblems.Problem(404, "not-found", "Not found");
            if (!await meter.HasRemainingAsync(db, read.Game.Access.WorkspaceId,
                    read.Entitlement?.Limit("monthly_delivery_mb"), ct))
                return ApiProblems.Problem(429, "delivery-quota-exceeded", "Game delivery allowance reached");
            var game = await db.Games.AsNoTracking().SingleAsync(g => g.Id == gameId, ct);
            if (game.ActiveReleaseId is not { } releaseId)
                return ApiProblems.Problem(404, "not-found", "Not found");
            var ticket = Token();
            var bridgeNonce = Token();
            var expiresAt = clock.GetUtcNow().AddMinutes(1);
            db.GameLaunches.Add(new GameLaunchRecord
            {
                Id = Guid.NewGuid(), GameId = gameId, ReleaseId = releaseId,
                Subject = subject, Preview = preview, TicketHash = Hash(ticket),
                TicketExpiresAt = expiresAt
            });
            await db.SaveChangesAsync(ct);
            http.Response.Headers.CacheControl = "no-store";
            var origin = delivery.Origin(gameId);
            return Results.Ok(new GameLaunchDto(
                origin + "/__launch#ticket=" + Uri.EscapeDataString(ticket), origin,
                bridgeNonce, expiresAt, subject is not null));
        }).RequireRateLimiting("launch");

        app.MapPost("/api/admin/review-requests/{requestId:guid}/preview-launch", async (
            Guid requestId, HttpContext http, IConfiguration config, StarportDbContext db,
            IWorkspaceEntitlements entitlements, GameDeliverySettings delivery,
            TimeProvider clock, CancellationToken ct) =>
        {
            if (!OperatorAuthorization.CanModerate(http.User, config))
                return ApiProblems.Problem(404, "not-found", "Not found");
            if (!delivery.Enabled)
                return ApiProblems.Problem(503, "delivery-unavailable", "Game delivery is unavailable");
            var request = await db.GameReviewRequests.AsNoTracking()
                .SingleOrDefaultAsync(r => r.Id == requestId, ct);
            var candidateEntitlement = request is null ? null :
                await CanModerateCandidateAsync(request, db, entitlements,
                    clock.GetUtcNow(), ct);
            if (candidateEntitlement is null)
                return ApiProblems.Problem(404, "not-found", "Not found");
            var ticket = Token();
            var bridgeNonce = Token();
            var expiresAt = clock.GetUtcNow().AddMinutes(1);
            db.GameLaunches.Add(new GameLaunchRecord
            {
                Id = Guid.NewGuid(), GameId = request!.GameId, ReleaseId = request.ReleaseId,
                Subject = http.User.Subject(), Preview = true, ReviewRequestId = request.Id,
                TicketHash = Hash(ticket), TicketExpiresAt = expiresAt
            });
            await db.SaveChangesAsync(ct);
            http.Response.Headers.CacheControl = "no-store";
            var origin = delivery.Origin(request.GameId);
            return Results.Ok(new GameLaunchDto(origin + "/__launch#ticket=" +
                Uri.EscapeDataString(ticket), origin, bridgeNonce, expiresAt, false));
        }).RequireAuthorization().RequireRateLimiting("launch");

        app.MapGet("/__launch", (HttpContext http, GameDeliverySettings delivery) =>
        {
            if (!delivery.TryGameHost(http.Request.Host.Host, out _))
                return ApiProblems.Problem(404, "not-found", "Not found");
            var nonce = Token();
            SetGameHeaders(http, delivery);
            http.Response.Headers.ContentSecurityPolicy =
                $"default-src 'none'; script-src 'nonce-{nonce}'; connect-src 'self'; " +
                $"frame-ancestors {delivery.AppOrigin}; base-uri 'none'; form-action 'none'";
            var html = "<!doctype html><meta charset=\"utf-8\"><title>Starting game</title>" +
                "<div id=\"status\">Starting game…</div>" +
                $"<script nonce=\"{nonce}\">" +
                "(async()=>{const p=new URLSearchParams(location.hash.slice(1));" +
                "const ticket=p.get('ticket');const origin=p.get('starport_origin');" +
                "const bridge=p.get('starport_nonce');history.replaceState(null,'',location.pathname);" +
                $"if(!ticket||origin!=={System.Text.Json.JsonSerializer.Serialize(delivery.AppOrigin)}||" +
                "!bridge)throw Error('Missing launch details');" +
                "const r=await fetch('/__session',{method:'POST',credentials:'same-origin'," +
                "headers:{'Content-Type':'application/json'},body:JSON.stringify({ticket})});" +
                "if(!r.ok)throw Error('Game access expired');" +
                "sessionStorage.setItem('starport.bridge.v1.origin',origin);" +
                "sessionStorage.setItem('starport.bridge.v1.nonce',bridge);" +
                "location.replace('/index.html');" +
                "})().catch(e=>document.getElementById('status').textContent=e.message);</script>";
            return Results.Content(html, "text/html; charset=utf-8");
        });

        app.MapPost("/__session", async (RedeemLaunchBody? body, HttpContext http,
            GameDeliverySettings delivery, GameReadService access, StarportDbContext db,
            TimeProvider clock, CancellationToken ct) =>
        {
            if (!delivery.TryGameHost(http.Request.Host.Host, out var gameId))
                return ApiProblems.Problem(404, "not-found", "Not found");
            if (body?.Ticket is not { Length: >= 32 and <= 128 } ticket)
                return ApiProblems.Problem(400, "invalid-ticket", "Invalid launch ticket");
            var hash = Hash(ticket);
            var now = clock.GetUtcNow();
            var launch = await db.GameLaunches.AsNoTracking().SingleOrDefaultAsync(l =>
                l.TicketHash == hash && l.GameId == gameId && l.RedeemedAt == null &&
                l.TicketExpiresAt > now, ct);
            if (launch is null) return ApiProblems.Problem(404, "launch-expired", "Launch expired");
            if (launch.ReviewRequestId is { } reviewId)
            {
                var request = await db.GameReviewRequests.AsNoTracking()
                    .SingleOrDefaultAsync(r => r.Id == reviewId && r.GameId == gameId &&
                        r.ReleaseId == launch.ReleaseId, ct);
                var candidateEntitlement = request is null ? null :
                    await CanModerateCandidateAsync(request, db,
                        http.RequestServices.GetRequiredService<IWorkspaceEntitlements>(), now, ct);
                if (candidateEntitlement is null)
                    return ApiProblems.Problem(404, "not-found", "Not found");
            }
            else
            {
                var read = await access.ReadAsync(gameId, launch.Subject, launch.Preview, ct);
                if (read.Game is null) return ApiProblems.Problem(404, "not-found", "Not found");
                var game = await db.Games.AsNoTracking().SingleAsync(g => g.Id == gameId, ct);
                if (game.ActiveReleaseId != launch.ReleaseId)
                    return ApiProblems.Problem(409, "release-changed", "Game release changed");
            }
            var session = Token();
            var updated = await db.GameLaunches.Where(l => l.Id == launch.Id &&
                l.RedeemedAt == null && l.TicketExpiresAt > now)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(l => l.SessionHash, Hash(session))
                    .SetProperty(l => l.RedeemedAt, now)
                    .SetProperty(l => l.SessionExpiresAt,
                        launch.ReviewRequestId == null ? now.AddHours(2) : now.AddMinutes(15)), ct);
            if (updated == 0) return ApiProblems.Problem(404, "launch-expired", "Launch expired");
            http.Response.Cookies.Append(CookieName(delivery), session, new CookieOptions
            {
                HttpOnly = true, Secure = delivery.Scheme == "https", SameSite = SameSiteMode.Lax,
                Path = "/", Expires = launch.ReviewRequestId == null
                    ? now.AddHours(2) : now.AddMinutes(15)
            });
            http.Response.Headers.CacheControl = "no-store";
            return Results.NoContent();
        }).RequireRateLimiting("launch");

        app.MapMethods("/{**filePath}", ["GET", "HEAD"], async (string? filePath, HttpContext http,
            GameDeliverySettings delivery, GameReadService access, StarportDbContext db,
            IServiceProvider services, IConfiguration config, DeliveryUsageMeter meter,
            TimeProvider clock, CancellationToken ct) =>
        {
            if (!delivery.TryGameHost(http.Request.Host.Host, out var gameId))
                return ApiProblems.Problem(404, "not-found", "Not found");
            var path = string.IsNullOrWhiteSpace(filePath) ? "index.html" : filePath;
            if (!PackagePath.IsSafe(path)) return ApiProblems.Problem(404, "not-found", "Not found");
            if (config["Storage:Mode"] is not ("local" or "gcs"))
                return ApiProblems.Problem(503, "delivery-unavailable", "Game delivery is unavailable");
            var session = http.Request.Cookies[CookieName(delivery)];
            if (session is not { Length: >= 32 and <= 128 })
                return ApiProblems.Problem(404, "not-found", "Not found");
            var now = clock.GetUtcNow();
            var launch = await db.GameLaunches.AsNoTracking().SingleOrDefaultAsync(l =>
                l.GameId == gameId && l.SessionHash == Hash(session) &&
                l.SessionExpiresAt > now, ct);
            if (launch is null) return ApiProblems.Problem(404, "not-found", "Not found");
            Starport.Domain.Access.PublishingEntitlement? currentEntitlement;
            if (launch.ReviewRequestId is { } reviewId)
            {
                var request = await db.GameReviewRequests.AsNoTracking()
                    .SingleOrDefaultAsync(r => r.Id == reviewId && r.GameId == gameId &&
                        r.ReleaseId == launch.ReleaseId, ct);
                currentEntitlement = request is null ? null :
                    await CanModerateCandidateAsync(request, db,
                        http.RequestServices.GetRequiredService<IWorkspaceEntitlements>(), now, ct);
                if (currentEntitlement is null)
                    return ApiProblems.Problem(404, "not-found", "Not found");
            }
            else
            {
                var read = await access.ReadAsync(gameId, launch.Subject, launch.Preview, ct);
                if (read.Game is null)
                    return ApiProblems.Problem(404, "not-found", "Not found");
                currentEntitlement = read.Entitlement;
            }
            var game = await db.Games.AsNoTracking().SingleAsync(g => g.Id == gameId, ct);
            if (launch.ReviewRequestId is null && game.ActiveReleaseId != launch.ReleaseId)
                return ApiProblems.Problem(404, "not-found", "Not found");
            var file = await db.ReleaseFiles.AsNoTracking().SingleOrDefaultAsync(f =>
                f.ReleaseId == launch.ReleaseId && f.Path == path, ct);
            if (file is null) return ApiProblems.Problem(404, "not-found", "Not found");
            var fullPath = config["Storage:Mode"] == "local"
                ? Path.Combine(services.GetRequiredService<StoragePaths>()
                    .Release(launch.ReleaseId), path.Replace('/', Path.DirectorySeparatorChar))
                : null;
            if (fullPath is not null && !File.Exists(fullPath))
                return ApiProblems.Problem(503, "file-unavailable", "Game file is unavailable");
            SetGameHeaders(http, delivery);
            http.Response.Headers.ContentSecurityPolicy =
                $"default-src 'self' data: blob:; script-src 'self' 'unsafe-inline' 'wasm-unsafe-eval'; " +
                $"style-src 'self' 'unsafe-inline'; connect-src 'self'; frame-ancestors {delivery.AppOrigin}; " +
                "base-uri 'none'; object-src 'none'; form-action 'none'";
            http.Response.Headers["Cross-Origin-Resource-Policy"] = "same-origin";
            if (!ContentTypes.TryGetContentType(path, out var contentType))
                contentType = "application/octet-stream";
            if (path.EndsWith(".pck", StringComparison.OrdinalIgnoreCase))
                contentType = "application/octet-stream";
            http.Response.Headers.AcceptRanges = "bytes";
            var rangeText = http.Request.Headers.Range.ToString();
            long? rangeStart = null;
            long? rangeEnd = null;
            if (!string.IsNullOrEmpty(rangeText))
            {
                if (!TrySingleRange(rangeText, file.Bytes, out var start, out var end))
                {
                    http.Response.Headers.ContentRange = $"bytes */{file.Bytes}";
                    return Results.StatusCode(StatusCodes.Status416RangeNotSatisfiable);
                }
                rangeStart = start;
                rangeEnd = end;
            }
            if (HttpMethods.IsHead(http.Request.Method))
            {
                http.Response.StatusCode = rangeStart is null ? 200 : 206;
                http.Response.ContentType = contentType;
                http.Response.ContentLength = rangeStart is null ? file.Bytes :
                    rangeEnd!.Value - rangeStart.Value + 1;
                if (rangeStart is not null)
                    http.Response.Headers.ContentRange = $"bytes {rangeStart}-{rangeEnd}/{file.Bytes}";
                return Results.Empty;
            }
            var servedBytes = rangeStart is null ? file.Bytes :
                rangeEnd!.Value - rangeStart.Value + 1;
            if (launch.ReviewRequestId is null &&
                !await meter.TryReserveAsync(db, game.WorkspaceId, servedBytes,
                    currentEntitlement?.Limit("monthly_delivery_mb"), ct))
                return ApiProblems.Problem(429, "delivery-quota-exceeded", "Game delivery allowance reached");
            if (config["Storage:Mode"] == "gcs")
            {
                var storage = services.GetRequiredService<GcsGameStorage>();
                http.Response.StatusCode = rangeStart is null ? 200 : 206;
                http.Response.ContentType = contentType;
                http.Response.ContentLength = rangeStart is null ? file.Bytes :
                    rangeEnd!.Value - rangeStart.Value + 1;
                if (rangeStart is not null)
                    http.Response.Headers.ContentRange = $"bytes {rangeStart}-{rangeEnd}/{file.Bytes}";
                await storage.DownloadAsync(storage.ReleaseBucket,
                    GcsGameStorage.ReleaseKey(launch.ReleaseId, path), http.Response.Body, ct,
                    rangeStart, rangeEnd);
                return Results.Empty;
            }
            return Results.File(fullPath!, contentType, enableRangeProcessing: true);
        });
    }

    private static void SetGameHeaders(HttpContext http, GameDeliverySettings delivery)
    {
        http.Response.Headers.CacheControl = "no-store";
        http.Response.Headers["Referrer-Policy"] = "no-referrer";
        http.Response.Headers["X-Content-Type-Options"] = "nosniff";
        http.Response.Headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
    }

    private static string Token() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static string CookieName(GameDeliverySettings delivery) =>
        delivery.Scheme == "https" ? SessionCookie : "starport-game";

    private static bool TrySingleRange(string text, long bytes, out long start, out long end)
    {
        start = end = 0;
        if (bytes <= 0 || !RangeHeaderValue.TryParse(text, out var range) ||
            !string.Equals(range.Unit, "bytes", StringComparison.OrdinalIgnoreCase) ||
            range.Ranges.Count != 1)
            return false;
        var part = range.Ranges.Single();
        if (part.From is { } from)
        {
            if (from >= bytes) return false;
            start = from;
            end = Math.Min(part.To ?? bytes - 1, bytes - 1);
            return end >= start;
        }
        if (part.To is not { } suffix || suffix <= 0) return false;
        start = Math.Max(0, bytes - suffix);
        end = bytes - 1;
        return true;
    }

    private static async Task<Starport.Domain.Access.PublishingEntitlement?> CanModerateCandidateAsync(
        GameReviewRequestRecord request,
        StarportDbContext db, IWorkspaceEntitlements entitlements,
        DateTimeOffset now, CancellationToken ct)
    {
        if (request.Status != GameReviewStatus.Pending) return null;
        var game = await db.Games.AsNoTracking().SingleOrDefaultAsync(g => g.Id == request.GameId, ct);
        if (game is null || game.IsModerationSuspended ||
            game.MetadataRevision != request.MetadataRevision ||
            game.Audience != Starport.Domain.Access.GameAudience.Public)
            return null;
        var release = await db.GameReleases.AsNoTracking().SingleOrDefaultAsync(r =>
            r.Id == request.ReleaseId && r.GameId == game.Id && r.IsValidated, ct);
        if (release is null || release.Sha256 != request.ReleaseSha256) return null;
        var entitlement = await entitlements.GetAsync(game.WorkspaceId, ct);
        return entitlement?.WorkspaceId == game.WorkspaceId && entitlement.IsActiveAt(now)
            ? entitlement : null;
    }
    private static string Hash(string value) => Convert.ToHexStringLower(
        SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
