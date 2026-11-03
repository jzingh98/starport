using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Starport.Api.Auth;
using Starport.Infrastructure.Accounts;
using Starport.Infrastructure.Database;

namespace Starport.Api.Endpoints;

public sealed record CreateWorkspaceBody(string? Slug, string? Name, WorkspaceKind Kind);
public sealed record WorkspaceDto(string Id, string Slug, string Name, WorkspaceKind Kind, string Role,
    bool CanPublish, DateTimeOffset? PaidThrough);
public sealed record SetPublishingBody(bool CanPublish, DateTimeOffset? PaidThrough, bool Suspended,
    Dictionary<string, int?>? Limits);

public static class WorkspaceEndpoints
{
    private static readonly Regex Slug = new("^[a-z0-9]+(?:-[a-z0-9]+)*$", RegexOptions.Compiled);

    public static void MapWorkspaceEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/workspaces").RequireAuthorization();

        group.MapGet("", async (HttpContext http, StarportDbContext db, IConfiguration config,
            IServiceProvider services, TimeProvider clock, CancellationToken ct) =>
        {
            var subject = http.User.Subject()!;
            if (config["Workspaces:Source"] == "accounts")
            {
                var hosted = await services.GetRequiredService<AccountsWorkspaceAccess>().ListAsync(subject, ct);
                var hostedItems = new List<WorkspaceDto>(hosted.Count);
                foreach (var workspace in hosted)
                {
                    if (!Enum.TryParse<WorkspaceKind>(workspace.Kind, true, out var kind))
                        throw new AccountsAccessUnavailableException("Accounts returned an invalid workspace kind.");
                    hostedItems.Add(new(workspace.Id, workspace.Slug, workspace.Name, kind,
                        workspace.Role.ToString(), workspace.CanPublish, workspace.PaidThrough));
                }
                return Results.Ok(hostedItems);
            }
            var now = clock.GetUtcNow();
            var items = await (from member in db.WorkspaceMembers.AsNoTracking()
                join workspace in db.Workspaces.AsNoTracking() on member.WorkspaceId equals workspace.Id
                join publishing in db.WorkspacePublishing.AsNoTracking() on workspace.Id equals publishing.WorkspaceId
                where member.Subject == subject
                orderby workspace.CreatedAt
                select new WorkspaceDto(workspace.Id, workspace.Slug, workspace.Name, workspace.Kind,
                    member.Role.ToString(), publishing.CanPublish && !publishing.Suspended &&
                    publishing.PaidThrough > now &&
                    member.Role != Starport.Domain.Access.WorkspaceRole.Viewer, publishing.PaidThrough))
                .ToListAsync(ct);
            return Results.Ok(items);
        });

        group.MapPost("", async (CreateWorkspaceBody? body, HttpContext http, StarportDbContext db,
            IConfiguration config, TimeProvider clock, CancellationToken ct) =>
        {
            if (config["Workspaces:Source"] != "local")
                return ApiProblems.Problem(404, "not-found", "Not found");
            if (body?.Slug is null || body.Name is null || body.Slug.Length is < 2 or > 80 ||
                !Slug.IsMatch(body.Slug) || string.IsNullOrWhiteSpace(body.Name) ||
                body.Name.Trim().Length > 200 || !Enum.IsDefined(body.Kind))
                return ApiProblems.Problem(400, "invalid-workspace", "Invalid workspace details");

            var subject = http.User.Subject()!;
            if (body.Kind == WorkspaceKind.Personal && await db.WorkspaceMembers.AnyAsync(m =>
                m.Subject == subject && m.Role == Starport.Domain.Access.WorkspaceRole.Owner &&
                db.Workspaces.Any(w => w.Id == m.WorkspaceId && w.Kind == WorkspaceKind.Personal), ct))
                return ApiProblems.Problem(409, "personal-workspace-exists", "A personal workspace already exists");

            var id = "ws_" + Guid.NewGuid().ToString("N");
            var now = clock.GetUtcNow();
            db.Workspaces.Add(new WorkspaceRecord
            {
                Id = id, Slug = body.Slug, Name = body.Name.Trim(), Kind = body.Kind, CreatedAt = now
            });
            db.WorkspaceMembers.Add(new WorkspaceMemberRecord
            {
                WorkspaceId = id, Subject = subject, Role = Starport.Domain.Access.WorkspaceRole.Owner
            });
            db.WorkspacePublishing.Add(new WorkspacePublishingRecord
            {
                WorkspaceId = id, CanPublish = false, LimitsJson = "{}"
            });
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateException error) when (error.InnerException is PostgresException { SqlState: "23505" })
            {
                return ApiProblems.Problem(409, "workspace-conflict", "Workspace name is already in use");
            }
            return Results.Created($"/api/workspaces/{id}", new WorkspaceDto(id, body.Slug, body.Name.Trim(),
                body.Kind, "Owner", false, null));
        });

        app.MapPut("/api/admin/workspaces/{workspaceId}/publishing", async (
            string workspaceId, SetPublishingBody? body, HttpContext http, StarportDbContext db,
            IConfiguration config, TimeProvider clock, CancellationToken ct) =>
        {
            var operators = config.GetSection("Operator:Subjects").Get<string[]>() ?? [];
            if (config["Workspaces:Source"] != "local" ||
                !operators.Contains(http.User.Subject(), StringComparer.Ordinal))
                return ApiProblems.Problem(404, "not-found", "Not found");
            if (body is null || body.Limits?.Any(x => x.Value < 0 || x.Key.Length > 64) == true)
                return ApiProblems.Problem(400, "invalid-publishing-grant", "Invalid publishing grant");
            var grant = await db.WorkspacePublishing.SingleOrDefaultAsync(e => e.WorkspaceId == workspaceId, ct);
            if (grant is null)
                return ApiProblems.Problem(404, "not-found", "Not found");
            grant.CanPublish = body.CanPublish;
            grant.PaidThrough = body.PaidThrough;
            grant.Suspended = body.Suspended;
            grant.LimitsJson = JsonSerializer.Serialize(body.Limits ?? new());
            if (!body.CanPublish || body.Suspended || body.PaidThrough <= clock.GetUtcNow())
            {
                var games = await db.Games.Where(g => g.WorkspaceId == workspaceId && g.IsPublished)
                    .ToListAsync(ct);
                foreach (var game in games)
                {
                    game.IsPublished = false;
                    game.UpdatedAt = clock.GetUtcNow();
                }
            }
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        }).RequireAuthorization();
    }
}
