using Microsoft.EntityFrameworkCore;
using Npgsql;
using Starport.Api.Auth;
using Starport.Application.Access;
using Starport.Application.Publishing;
using Starport.Domain.Access;
using Starport.Infrastructure.Database;

namespace Starport.Api.Endpoints;

public sealed record CreateGameBody(string? Slug, string? Title, string? Description);
public sealed record GameCardDto(Guid Id, string Slug, string Title, string Description);

public static class GameEndpoints
{
    public static void MapGameEndpoints(this WebApplication app)
    {
        var studio = app.MapGroup("/api/workspaces/{workspaceId}/games").RequireAuthorization();
        studio.MapPost("", async (string workspaceId, CreateGameBody? body, HttpContext http,
            GameDraftService service, CancellationToken ct) =>
        {
            if (body is null)
                return ApiProblems.Problem(400, "invalid-game", "Invalid game details");
            try
            {
                var result = await service.CreateAsync(new CreateGameDraft(workspaceId, body.Slug ?? "",
                    body.Title ?? "", body.Description ?? ""), http.User.Subject(), ct);
                return result.Decision switch
                {
                    DraftCreationDecision.Created => Results.Created(
                        $"/api/workspaces/{workspaceId}/games/{result.Game!.Id}", result.Game),
                    DraftCreationDecision.NotFound => ApiProblems.Problem(404, "not-found", "Not found"),
                    DraftCreationDecision.Forbidden => ApiProblems.Problem(403, "publisher-role-required", "Publishing role required"),
                    DraftCreationDecision.PublisherOffline => ApiProblems.Problem(402, "publishing-plan-required", "Publishing plan required"),
                    DraftCreationDecision.QuotaExceeded => ApiProblems.Problem(402, "quota-exceeded", "Game limit reached"),
                    _ => ApiProblems.Problem(401, "sign-in-required", "Sign in required")
                };
            }
            catch (ArgumentException)
            {
                return ApiProblems.Problem(400, "invalid-game", "Invalid game details");
            }
            catch (DbUpdateException error) when (error.InnerException is PostgresException { SqlState: "23505" })
            {
                return ApiProblems.Problem(409, "game-slug-conflict", "Game slug is already in use");
            }
            catch (Exception error) when (error is PostgresException { SqlState: "40001" } ||
                error is DbUpdateException { InnerException: PostgresException { SqlState: "40001" } })
            {
                return ApiProblems.Problem(409, "game-create-conflict", "A game was created concurrently; retry");
            }
        });

        studio.MapGet("", async (string workspaceId, HttpContext http, GameDraftService service,
            CancellationToken ct) =>
        {
            var games = await service.ListAsync(workspaceId, http.User.Subject(), ct);
            return games is null ? ApiProblems.Problem(404, "not-found", "Not found") : Results.Ok(games);
        });

        app.MapGet("/api/games/{gameId:guid}", async (Guid gameId, HttpContext http,
            GameReadService service, CancellationToken ct) =>
        {
            var read = await service.ReadAsync(gameId, http.User.Subject(),
                preview: http.Request.Query["preview"] == "true", cancellationToken: ct);
            if (read.Game is null)
                return read.Decision == AccessDecision.DeliveryQuotaExceeded
                    ? ApiProblems.Problem(429, "delivery-quota-exceeded", "Game delivery allowance reached")
                    : ApiProblems.Problem(404, "not-found", "Not found");
            return Results.Ok(new GameCardDto(read.Game.Access.Id, read.Game.Slug,
                read.Game.Title, read.Game.Description));
        });

        app.MapGet("/api/games", async (int? page, GameCatalogService catalog, CancellationToken ct) =>
        {
            if (page is < 0 or > 100)
                return ApiProblems.Problem(400, "invalid-page", "Page must be between 0 and 100");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            try
            {
                var games = await catalog.ListAsync(page ?? 0, timeout.Token);
                return Results.Ok(games.Select(game => new GameCardDto(game.Access.Id,
                    game.Slug, game.Title, game.Description)).ToList());
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return ApiProblems.Problem(503, "catalog-unavailable", "Games are unavailable right now");
            }
        });
    }
}
