using System.Net;
using System.Text;
using Microsoft.Extensions.Options;
using Starport.Infrastructure.Accounts;

namespace Starport.Tests.Access;

public sealed class AccountsWorkspaceAccessTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Availability_uses_the_earliest_authoritative_boundary_and_rechecks_every_call()
    {
        var availabilityCalls = 0;
        var handler = new ReplyHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/token"))
                return Json("{\"access_token\":\"service-token\",\"expires_in\":300}");
            Assert.Equal("Bearer service-token", request.Headers.Authorization?.ToString());
            availabilityCalls++;
            return Json("[{\"workspaceId\":\"ws_creator\",\"canServe\":true," +
                "\"limits\":{\"games\":2},\"paidThrough\":\"2026-11-03T00:00:00Z\"," +
                "\"validUntil\":\"2026-11-02T13:00:00Z\"}]");
        });
        var access = Create(handler);

        var first = await access.GetAsync("ws_creator");
        var second = await access.GetAsync("ws_creator");
        Assert.True(first!.IsActiveAt(Now));
        Assert.False(first.IsActiveAt(Now.AddHours(1)));
        Assert.Equal(2, first.Limit("games"));
        Assert.Equal(first.PaidThrough, second?.PaidThrough);
        Assert.Equal(first.Limit("games"), second?.Limit("games"));
        Assert.Equal(2, availabilityCalls);
    }

    [Fact]
    public async Task Resolve_returns_current_role_and_nonmember_is_hidden()
    {
        var found = true;
        var handler = new ReplyHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/token"))
                return Json("{\"access_token\":\"service-token\",\"expires_in\":300}");
            return found ? Json("{\"workspaceId\":\"ws_creator\",\"role\":\"Viewer\"}")
                : new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        var access = Create(handler);

        Assert.Equal(Starport.Domain.Access.WorkspaceRole.Viewer,
            (await access.GetAsync("ws_creator", "player"))?.Role);
        found = false;
        Assert.Null(await access.GetAsync("ws_creator", "player"));
    }

    [Fact]
    public async Task Provider_failure_does_not_turn_into_a_local_grant()
    {
        var handler = new ReplyHandler(request => request.RequestUri!.AbsolutePath.EndsWith("/token")
            ? Json("{\"access_token\":\"service-token\",\"expires_in\":300}")
            : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        var access = Create(handler);
        await Assert.ThrowsAsync<AccountsAccessUnavailableException>(() => access.GetAsync("ws_creator"));
    }

    private static AccountsWorkspaceAccess Create(HttpMessageHandler handler)
    {
        var factory = new ClientFactory(handler);
        var options = Options.Create(new AccountsAccessOptions
        {
            BaseUrl = "https://accounts.example/", TokenUrl = "https://identity.example/token",
            ClientId = "starport-service", ClientSecret = "test-secret"
        });
        var tokens = new AccountsServiceTokenProvider(factory, options, new FixedClock());
        return new AccountsWorkspaceAccess(factory, tokens, options);
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    private sealed class ClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class ReplyHandler(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(reply(request));
    }

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
