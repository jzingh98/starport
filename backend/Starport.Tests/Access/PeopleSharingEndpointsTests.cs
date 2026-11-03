using System.Net;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Starport.Api.Endpoints;
using Starport.Application.Access;
using Starport.Domain.Access;
using Starport.Infrastructure.Accounts;
using Starport.Infrastructure.Database;

namespace Starport.Tests.Access;

public sealed class PeopleSharingEndpointsTests
{
    private const string Workspace = "ws_creator";
    private static readonly Guid GameId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Viewer_cannot_enumerate_or_change_people_shares()
    {
        await using var db = Database();
        var members = new Memberships(WorkspaceRole.Viewer);
        var access = new Entitlements();
        var config = Config("accounts");
        using var services = new ServiceCollection().BuildServiceProvider();

        var list = await PeopleSharingEndpoints.ListAsync(Workspace, GameId, Http(), db, members, default);
        var add = await PeopleSharingEndpoints.UpsertAsync(Workspace, GameId,
            new ShareWithPersonBody("player@example.com", null, null), Http(), db, members, access,
            config, services, new FixedClock(), default);
        Assert.Equal(404, Status(list));
        Assert.Equal(404, Status(add));
        Assert.Empty(db.GamePlayGrants);
    }

    [Fact]
    public async Task Paid_editor_resolves_verified_email_and_can_list_then_revoke_the_grant()
    {
        await using var db = Database();
        var handler = new ReplyHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/token"))
                return Json("{\"access_token\":\"service-token\",\"expires_in\":300}");
            Assert.Equal("Bearer service-token", request.Headers.Authorization?.ToString());
            Assert.Equal("/v1/entitlements/identity/resolve", request.RequestUri.AbsolutePath);
            return Json("{\"accountSub\":\"verified-player-id\"}");
        });
        var factory = new ClientFactory(handler);
        var options = Options.Create(new AccountsAccessOptions
        {
            BaseUrl = "https://accounts.example/", TokenUrl = "https://identity.example/token",
            ClientId = "starport-service", ClientSecret = "test-secret"
        });
        using var services = new ServiceCollection()
            .AddSingleton<IHttpClientFactory>(factory)
            .AddSingleton<IOptions<AccountsAccessOptions>>(options)
            .AddSingleton(new AccountsServiceTokenProvider(factory, options, new FixedClock()))
            .BuildServiceProvider();
        var members = new Memberships(WorkspaceRole.Member);
        var entitlements = new Entitlements();

        var added = await PeopleSharingEndpoints.UpsertAsync(Workspace, GameId,
            new ShareWithPersonBody("player@example.com", null, null), Http(), db, members,
            entitlements, Config("accounts"), services, new FixedClock(), default);
        Assert.Equal(200, Status(added));
        var stored = await db.GamePlayGrants.SingleAsync();
        Assert.Equal("verified-player-id", stored.Subject);
        Assert.Equal("player@example.com", stored.Email);
        Assert.Equal(200, Status(await PeopleSharingEndpoints.ListAsync(
            Workspace, GameId, Http(), db, members, default)));
        Assert.Equal(204, Status(await PeopleSharingEndpoints.RevokeAsync(
            Workspace, GameId, "verified-player-id", Http(), db, members, entitlements,
            new FixedClock(), default)));
        Assert.Empty(db.GamePlayGrants);
    }

    [Fact]
    public async Task Free_editor_cannot_share_and_local_mode_accepts_configured_email_or_subject()
    {
        await using var db = Database();
        var members = new Memberships(WorkspaceRole.Owner);
        var entitlements = new Entitlements { Value = new(Workspace, false, null) };
        using var services = new ServiceCollection().BuildServiceProvider();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Workspaces:Source"] = "local",
            ["Sharing:LocalUsers:friend@example.com"] = "friend-id"
        }).Build();

        Assert.Equal(402, Status(await PeopleSharingEndpoints.UpsertAsync(Workspace, GameId,
            new ShareWithPersonBody("friend@example.com", null, null), Http(), db, members,
            entitlements, config, services, new FixedClock(), default)));
        entitlements.Value = new(Workspace, true, Now.AddHours(1));
        Assert.Equal(200, Status(await PeopleSharingEndpoints.UpsertAsync(Workspace, GameId,
            new ShareWithPersonBody("friend@example.com", null, null), Http(), db, members,
            entitlements, config, services, new FixedClock(), default)));
        Assert.Equal(200, Status(await PeopleSharingEndpoints.UpsertAsync(Workspace, GameId,
            new ShareWithPersonBody(null, "another-player", null), Http(), db, members,
            entitlements, config, services, new FixedClock(), default)));
        Assert.Equal(["another-player", "friend-id"],
            (await db.GamePlayGrants.OrderBy(g => g.Subject).Select(g => g.Subject).ToListAsync()));
        Assert.Equal("friend@example.com", (await db.GamePlayGrants.SingleAsync(g => g.Subject == "friend-id")).Email);
        Assert.Null((await db.GamePlayGrants.SingleAsync(g => g.Subject == "another-player")).Email);
    }

    [Fact]
    public async Task Active_person_shares_are_limited_to_one_hundred_per_game()
    {
        await using var db = Database();
        for (var index = 0; index < 100; index++)
            db.GamePlayGrants.Add(new GamePlayGrantRecord
            {
                GameId = GameId, Subject = $"player-{index}", ExpiresAt = null
            });
        await db.SaveChangesAsync();
        var members = new Memberships(WorkspaceRole.Owner);
        var entitlements = new Entitlements();
        using var services = new ServiceCollection().BuildServiceProvider();

        Assert.Equal(409, Status(await PeopleSharingEndpoints.UpsertAsync(Workspace, GameId,
            new ShareWithPersonBody(null, "player-101", null), Http(), db, members,
            entitlements, Config("local"), services, new FixedClock(), default)));
        Assert.Equal(100, await db.GamePlayGrants.CountAsync());
        Assert.Equal(200, Status(await PeopleSharingEndpoints.UpsertAsync(Workspace, GameId,
            new ShareWithPersonBody(null, "player-0", Now.AddDays(1)), Http(), db, members,
            entitlements, Config("local"), services, new FixedClock(), default)));
    }

    private static StarportDbContext Database()
    {
        var options = new DbContextOptionsBuilder<StarportDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        var db = new StarportDbContext(options);
        db.Games.Add(new GameRecord
        {
            Id = GameId, WorkspaceId = Workspace, Slug = "test-game", Title = "Test", Description = "Test",
            Audience = GameAudience.People, CreatedAt = Now, UpdatedAt = Now
        });
        db.SaveChanges();
        return db;
    }

    private static DefaultHttpContext Http()
    {
        var context = new DefaultHttpContext();
        context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "editor")], "test"));
        return context;
    }

    private static IConfiguration Config(string source) => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["Workspaces:Source"] = source }).Build();

    private static int? Status(IResult result) => (result as IStatusCodeHttpResult)?.StatusCode;

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    private sealed class Memberships(WorkspaceRole role) : IWorkspaceMemberships
    {
        public Task<WorkspaceMembership?> GetAsync(string workspaceId, string subject,
            CancellationToken cancellationToken = default) => Task.FromResult<WorkspaceMembership?>(
            workspaceId == Workspace && subject == "editor" ? new(Workspace, subject, role) : null);
    }

    private sealed class Entitlements : IWorkspaceEntitlements
    {
        public PublishingEntitlement? Value { get; set; } = new(Workspace, true, Now.AddHours(1));
        public Task<PublishingEntitlement?> GetAsync(string workspaceId,
            CancellationToken cancellationToken = default) => Task.FromResult(Value);
    }

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class ClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class ReplyHandler(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(reply(request));
    }
}
