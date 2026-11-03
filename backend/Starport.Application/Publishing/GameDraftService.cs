using System.Text.RegularExpressions;
using Starport.Application.Access;
using Starport.Domain.Access;

namespace Starport.Application.Publishing;

public sealed record GameDraft(Guid Id, string WorkspaceId, string Slug, string Title, string Description,
    GameAudience Audience, DateTimeOffset CreatedAt, bool IsPublished = false,
    bool HasValidatedRelease = false);

public sealed record CreateGameDraft(string WorkspaceId, string Slug, string Title, string Description);

public enum DraftCreationDecision { Created, SignInRequired, NotFound, Forbidden, PublisherOffline, QuotaExceeded }

public sealed record DraftCreationResult(DraftCreationDecision Decision, GameDraft? Game = null);

public interface IGameDraftStore
{
    Task<int> CountAsync(string workspaceId, CancellationToken cancellationToken = default);
    Task<GameDraft> CreateAsync(GameDraft draft, CancellationToken cancellationToken = default);
    async Task<GameDraft?> CreateWithinLimitAsync(GameDraft draft, int? limit,
        CancellationToken cancellationToken = default)
    {
        if (limit is { } maximum && await CountAsync(draft.WorkspaceId, cancellationToken) >= maximum)
            return null;
        return await CreateAsync(draft, cancellationToken);
    }
    Task<IReadOnlyList<GameDraft>> ListAsync(string workspaceId, CancellationToken cancellationToken = default);
}

/// <summary>Creates a private, unpublished draft after current owning-workspace checks.</summary>
public sealed class GameDraftService(
    IGameDraftStore drafts,
    IWorkspaceEntitlements entitlements,
    IWorkspaceMemberships memberships,
    TimeProvider clock)
{
    private static readonly Regex SlugPattern = new("^[a-z0-9]+(?:-[a-z0-9]+)*$", RegexOptions.Compiled);

    public async Task<DraftCreationResult> CreateAsync(
        CreateGameDraft request, string? subject, CancellationToken cancellationToken = default)
    {
        Validate(request);
        if (subject is null)
            return new(DraftCreationDecision.SignInRequired);

        var membership = await memberships.GetAsync(request.WorkspaceId, subject, cancellationToken);
        var entitlement = await entitlements.GetAsync(request.WorkspaceId, cancellationToken);
        var access = GameAccess.CanPublish(request.WorkspaceId, subject, membership, entitlement, clock.GetUtcNow());
        if (access != AccessDecision.Allowed)
            return new(access switch
            {
                AccessDecision.NotFound => DraftCreationDecision.NotFound,
                AccessDecision.Forbidden => DraftCreationDecision.Forbidden,
                _ => DraftCreationDecision.PublisherOffline
            });

        var draft = new GameDraft(Guid.NewGuid(), request.WorkspaceId, request.Slug, request.Title.Trim(),
            request.Description.Trim(), GameAudience.Private, clock.GetUtcNow());
        var created = await drafts.CreateWithinLimitAsync(draft, entitlement?.Limit("games"),
            cancellationToken);
        return created is null ? new(DraftCreationDecision.QuotaExceeded) :
            new(DraftCreationDecision.Created, created);
    }

    public async Task<IReadOnlyList<GameDraft>?> ListAsync(
        string workspaceId, string? subject, CancellationToken cancellationToken = default)
    {
        if (subject is null)
            return null;
        var membership = await memberships.GetAsync(workspaceId, subject, cancellationToken);
        return membership is { Role: not WorkspaceRole.Viewer }
            ? await drafts.ListAsync(workspaceId, cancellationToken)
            : null;
    }

    private static void Validate(CreateGameDraft request)
    {
        if (string.IsNullOrWhiteSpace(request.WorkspaceId) || request.WorkspaceId.Length > 64 ||
            string.IsNullOrWhiteSpace(request.Slug) || request.Slug.Length > 120 ||
            !SlugPattern.IsMatch(request.Slug) || string.IsNullOrWhiteSpace(request.Title) ||
            request.Title.Trim().Length > 200 || request.Description.Length > 2000)
            throw new ArgumentException("Invalid game draft fields.", nameof(request));
    }
}
