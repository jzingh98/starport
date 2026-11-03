using System.Net;
using System.Net.Http.Json;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Starport.Domain.Access;
using Starport.Api.Storage;
using Starport.Infrastructure.Database;

namespace Starport.Tests.Access;

public sealed class PublicReleaseWorkflowPostgresTests
{
    [Fact]
    public async Task Candidate_needs_review_before_activation_and_old_public_release_stays_live()
    {
        var connection = Environment.GetEnvironmentVariable("STARPORT_TEST_DATABASE");
        if (string.IsNullOrWhiteSpace(connection)) return;
        await using (var migration = new StarportDbContext(
            new DbContextOptionsBuilder<StarportDbContext>().UseNpgsql(connection).Options))
            await migration.Database.MigrateAsync();

        var workspaceId = "ws_" + Guid.NewGuid().ToString("N");
        var gameId = Guid.NewGuid();
        var activeId = Guid.NewGuid();
        var candidateId = Guid.NewGuid();
        var scratch = Path.Combine(Path.GetTempPath(), "starport-review-test-" + Guid.NewGuid().ToString("N"));
        var oldHtml = "<html>old release</html>";
        var candidateHtml = "<html>new candidate</html>";
        Directory.CreateDirectory(Path.Combine(scratch, "releases", activeId.ToString("N")));
        Directory.CreateDirectory(Path.Combine(scratch, "releases", candidateId.ToString("N")));
        await File.WriteAllTextAsync(Path.Combine(scratch, "releases", activeId.ToString("N"),
            "index.html"), oldHtml);
        await File.WriteAllTextAsync(Path.Combine(scratch, "releases", candidateId.ToString("N"),
            "index.html"), candidateHtml);
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Database"] = connection,
                    ["Database:Migrate"] = "skip",
                    ["Auth:Mode"] = "development",
                    ["Auth:Development:Subject"] = "release-reviewer",
                    ["Operator:Subjects:0"] = "release-reviewer",
                    ["Workspaces:Source"] = "local",
                    ["Storage:Mode"] = "local",
                    ["Storage:Root"] = scratch,
                    ["Processing:Mode"] = "none",
                    ["Reconciliation:Enabled"] = "false",
                    ["GameDelivery:RootDomain"] = "localhost",
                    ["GameDelivery:Enabled"] = "true",
                    ["GameDelivery:Scheme"] = "http",
                    ["GameDelivery:AppOrigin"] = "http://localhost"
                }));
            builder.ConfigureServices(services => services.AddSingleton<StoragePaths>());
        });
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = false
        });
        Assert.Equal(connection,
            factory.Services.GetRequiredService<IConfiguration>().GetConnectionString("Database"));
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<StarportDbContext>();
        Assert.Equal(new Npgsql.NpgsqlConnectionStringBuilder(connection).Database,
            db.Database.GetDbConnection().Database);
        var now = DateTimeOffset.UtcNow;
        db.Workspaces.Add(new WorkspaceRecord
        {
            Id = workspaceId, Slug = workspaceId, Name = "Release review test",
            Kind = WorkspaceKind.Personal, CreatedAt = now
        });
        db.WorkspaceMembers.Add(new WorkspaceMemberRecord
        {
            WorkspaceId = workspaceId, Subject = "release-reviewer", Role = WorkspaceRole.Owner
        });
        db.WorkspacePublishing.Add(new WorkspacePublishingRecord
        {
            WorkspaceId = workspaceId, CanPublish = true,
            PaidThrough = now.AddHours(2), LimitsJson = "{}"
        });
        db.Games.Add(new GameRecord
        {
            Id = gameId, WorkspaceId = workspaceId,
            Slug = "release-" + gameId.ToString("N"),
            Title = "Release review test", Description = "Test",
            Audience = GameAudience.Public, IsPublished = true,
            ActiveReleaseId = activeId, MetadataRevision = 1,
            CreatedAt = now, UpdatedAt = now
        });
        var oldSha = new string('a', 64);
        var newSha = new string('b', 64);
        db.GameReleases.AddRange(
            new GameReleaseRecord
            {
                Id = activeId, GameId = gameId, Sha256 = oldSha,
                StorageKey = "releases/old", IsValidated = true,
                CreatedAt = now.AddMinutes(-1), ValidatedAt = now.AddMinutes(-1)
            },
            new GameReleaseRecord
            {
                Id = candidateId, GameId = gameId, Sha256 = newSha,
                StorageKey = "releases/new", IsValidated = true,
                CreatedAt = now, ValidatedAt = now
            });
        db.ReleaseFiles.AddRange(
            new ReleaseFileRecord { ReleaseId = activeId, Path = "index.html",
                Bytes = Encoding.UTF8.GetByteCount(oldHtml),
                Sha256 = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(oldHtml))) },
            new ReleaseFileRecord { ReleaseId = candidateId, Path = "index.html",
                Bytes = Encoding.UTF8.GetByteCount(candidateHtml),
                Sha256 = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(candidateHtml))) });
        db.GameReviewApprovals.Add(new GameReviewApprovalRecord
        {
            Id = Guid.NewGuid(), GameId = gameId, ReleaseId = activeId,
            ReleaseSha256 = oldSha, MetadataRevision = 1,
            ReviewerSubject = "release-reviewer", ApprovedAt = now
        });
        await db.SaveChangesAsync();

        var studioBase = $"/api/workspaces/{workspaceId}/games/{gameId}";
        try
        {
            var releases = await client.GetAsync(studioBase + "/releases");
            Assert.Equal(HttpStatusCode.OK, releases.StatusCode);
            using (var body = JsonDocument.Parse(await releases.Content.ReadAsStringAsync()))
            {
                var rows = body.RootElement.EnumerateArray().ToArray();
                Assert.Equal(2, rows.Length);
                Assert.Equal(candidateId.ToString(), rows[0].GetProperty("id").GetString());
                Assert.Equal("NotRequested", rows[0].GetProperty("reviewStatus").GetString());
                Assert.Equal(Encoding.UTF8.GetByteCount(candidateHtml),
                    rows[0].GetProperty("bytes").GetInt64());
                Assert.Equal("Approved", rows[1].GetProperty("reviewStatus").GetString());
            }

            var blocked = await client.PutAsJsonAsync(studioBase + "/active-release",
                new { releaseId = candidateId });
            Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
            Assert.Equal(activeId, (await db.Games.AsNoTracking().SingleAsync(g => g.Id == gameId)).ActiveReleaseId);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/games/{gameId}")).StatusCode);

            var publicLaunch = await client.PostAsync($"/api/games/{gameId}/launch", null);
            Assert.True(publicLaunch.StatusCode == HttpStatusCode.OK,
                await publicLaunch.Content.ReadAsStringAsync());
            using var publicLaunchBody = JsonDocument.Parse(await publicLaunch.Content.ReadAsStringAsync());
            var publicTicket = publicLaunchBody.RootElement.GetProperty("embedUrl")
                .GetString()!.Split("#ticket=", 2)[1];
            var gameHost = $"g-{gameId:N}.localhost";
            var publicRedeem = await RedeemAsync(client, gameHost, publicTicket);
            Assert.Equal(HttpStatusCode.NoContent, publicRedeem.StatusCode);
            var publicCookie = publicRedeem.Headers.GetValues("Set-Cookie").Single().Split(';')[0];
            Assert.Equal(oldHtml, await (await GameGetAsync(client, gameHost, publicCookie))
                .Content.ReadAsStringAsync());

            var submit = await client.PostAsJsonAsync(studioBase + "/review-requests",
                new { releaseId = candidateId });
            Assert.Equal(HttpStatusCode.Created, submit.StatusCode);
            using var requestBody = JsonDocument.Parse(await submit.Content.ReadAsStringAsync());
            var requestId = requestBody.RootElement.GetProperty("id").GetGuid();
            Assert.Equal("Pending", requestBody.RootElement.GetProperty("status").GetString());
            Assert.Equal(HttpStatusCode.Conflict,
                (await client.PutAsJsonAsync(studioBase + "/active-release",
                    new { releaseId = candidateId })).StatusCode);
            Assert.Equal(activeId, (await db.Games.AsNoTracking().SingleAsync(g => g.Id == gameId)).ActiveReleaseId);

            var preview = await client.PostAsync(
                $"/api/admin/review-requests/{requestId}/preview-launch", null);
            Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
            using var previewBody = JsonDocument.Parse(await preview.Content.ReadAsStringAsync());
            Assert.False(previewBody.RootElement.GetProperty("canSave").GetBoolean());
            var previewTicket = previewBody.RootElement.GetProperty("embedUrl")
                .GetString()!.Split("#ticket=", 2)[1];
            var previewRedeem = await RedeemAsync(client, gameHost, previewTicket);
            Assert.Equal(HttpStatusCode.NoContent, previewRedeem.StatusCode);
            var previewCookie = previewRedeem.Headers.GetValues("Set-Cookie").Single().Split(';')[0];
            Assert.Equal(candidateHtml, await (await GameGetAsync(client, gameHost, previewCookie))
                .Content.ReadAsStringAsync());
            Assert.Equal(oldHtml, await (await GameGetAsync(client, gameHost, publicCookie))
                .Content.ReadAsStringAsync());

            var head = GameRequest(HttpMethod.Head, gameHost, previewCookie);
            var headResponse = await client.SendAsync(head);
            Assert.Equal(HttpStatusCode.OK, headResponse.StatusCode);
            Assert.Equal(Encoding.UTF8.GetByteCount(candidateHtml),
                headResponse.Content.Headers.ContentLength);
            var ranged = GameRequest(HttpMethod.Get, gameHost, previewCookie);
            ranged.Headers.Range = new RangeHeaderValue(0, 4);
            var rangedResponse = await client.SendAsync(ranged);
            Assert.Equal(HttpStatusCode.PartialContent, rangedResponse.StatusCode);
            Assert.Equal(candidateHtml[..5], await rangedResponse.Content.ReadAsStringAsync());
            var invalidRange = GameRequest(HttpMethod.Get, gameHost, previewCookie);
            invalidRange.Headers.Range = new RangeHeaderValue(999, 1000);
            Assert.Equal(HttpStatusCode.RequestedRangeNotSatisfiable,
                (await client.SendAsync(invalidRange)).StatusCode);

            var queue = await client.GetAsync("/api/admin/review-requests");
            Assert.Equal(HttpStatusCode.OK, queue.StatusCode);
            using (var queueBody = JsonDocument.Parse(await queue.Content.ReadAsStringAsync()))
                Assert.Contains(queueBody.RootElement.EnumerateArray(),
                    item => item.GetProperty("id").GetGuid() == requestId);

            var decision = await client.PostAsJsonAsync(
                $"/api/admin/review-requests/{requestId}/decision",
                new { approved = true, reason = (string?)null });
            Assert.Equal(HttpStatusCode.OK, decision.StatusCode);
            Assert.Equal(HttpStatusCode.NotFound,
                (await GameGetAsync(client, gameHost, previewCookie)).StatusCode);
            Assert.Equal(oldHtml, await (await GameGetAsync(client, gameHost, publicCookie))
                .Content.ReadAsStringAsync());
            var activate = await client.PutAsJsonAsync(studioBase + "/active-release",
                new { releaseId = candidateId });
            Assert.Equal(HttpStatusCode.OK, activate.StatusCode);
            using (var activeBody = JsonDocument.Parse(await activate.Content.ReadAsStringAsync()))
            {
                Assert.Equal(candidateId, activeBody.RootElement.GetProperty("releaseId").GetGuid());
                Assert.True(activeBody.RootElement.GetProperty("isPublished").GetBoolean());
                Assert.True(activeBody.RootElement.GetProperty("publicReviewApproved").GetBoolean());
            }
            Assert.Equal(candidateId,
                (await db.Games.AsNoTracking().SingleAsync(g => g.Id == gameId)).ActiveReleaseId);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/games/{gameId}")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound,
                (await GameGetAsync(client, gameHost, publicCookie)).StatusCode);

            var candidateLaunch = await client.PostAsync($"/api/games/{gameId}/launch", null);
            Assert.Equal(HttpStatusCode.OK, candidateLaunch.StatusCode);
            using var candidateLaunchBody = JsonDocument.Parse(
                await candidateLaunch.Content.ReadAsStringAsync());
            var candidateTicket = candidateLaunchBody.RootElement.GetProperty("embedUrl")
                .GetString()!.Split("#ticket=", 2)[1];
            var candidateRedeem = await RedeemAsync(client, gameHost, candidateTicket);
            Assert.Equal(HttpStatusCode.NoContent, candidateRedeem.StatusCode);
            var candidateCookie = candidateRedeem.Headers.GetValues("Set-Cookie").Single().Split(';')[0];
            Assert.Equal(candidateHtml,
                await (await GameGetAsync(client, gameHost, candidateCookie)).Content.ReadAsStringAsync());

            var report = await client.PostAsJsonAsync($"/api/games/{gameId}/reports",
                new { category = "abuse", description = "Test report" });
            Assert.True(report.StatusCode == HttpStatusCode.Created,
                await report.Content.ReadAsStringAsync());
            using var reportBody = JsonDocument.Parse(await report.Content.ReadAsStringAsync());
            var reportId = reportBody.RootElement.GetProperty("id").GetGuid();
            var reports = await client.GetAsync("/api/admin/reports");
            Assert.Equal(HttpStatusCode.OK, reports.StatusCode);
            using (var reportsBody = JsonDocument.Parse(await reports.Content.ReadAsStringAsync()))
                Assert.Contains(reportsBody.RootElement.EnumerateArray(),
                    item => item.GetProperty("id").GetGuid() == reportId);

            var suspend = await client.PutAsJsonAsync($"/api/admin/games/{gameId}/suspension",
                new { suspended = true, reason = "Safety test" });
            Assert.Equal(HttpStatusCode.OK, suspend.StatusCode);
            Assert.Equal(HttpStatusCode.NotFound,
                (await GameGetAsync(client, gameHost, candidateCookie)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound,
                (await client.GetAsync($"/api/games/{gameId}")).StatusCode);
            var suspendedGame = await db.Games.AsNoTracking().SingleAsync(g => g.Id == gameId);
            Assert.True(suspendedGame.IsModerationSuspended);
            Assert.False(suspendedGame.IsPublished);
            var suspendedQueue = await client.GetAsync("/api/admin/reports");
            using (var queueBody = JsonDocument.Parse(await suspendedQueue.Content.ReadAsStringAsync()))
            {
                var matching = queueBody.RootElement.EnumerateArray().Single(item =>
                    item.GetProperty("id").GetGuid() == reportId);
                Assert.True(matching.GetProperty("isModerationSuspended").GetBoolean());
            }

            var resolve = await client.PostAsJsonAsync($"/api/admin/reports/{reportId}/resolve",
                new { resolution = "Reviewed in test" });
            Assert.Equal(HttpStatusCode.NoContent, resolve.StatusCode);
            var unsuspend = await client.PutAsJsonAsync($"/api/admin/games/{gameId}/suspension",
                new { suspended = false, reason = "Review completed" });
            Assert.Equal(HttpStatusCode.OK, unsuspend.StatusCode);
            Assert.Equal(HttpStatusCode.NotFound,
                (await client.GetAsync($"/api/games/{gameId}")).StatusCode);
            Assert.Equal(2, await db.GameModerationActions.CountAsync(a => a.GameId == gameId));
        }
        finally
        {
            db.ChangeTracker.Clear();
            await db.Games.Where(g => g.Id == gameId).ExecuteDeleteAsync();
            await db.Workspaces.Where(w => w.Id == workspaceId).ExecuteDeleteAsync();
            Directory.Delete(scratch, recursive: true);
        }
    }

    private static HttpRequestMessage GameRequest(HttpMethod method, string host, string cookie)
    {
        var request = new HttpRequestMessage(method, "/index.html");
        request.Headers.Host = host;
        request.Headers.TryAddWithoutValidation("Cookie", cookie);
        return request;
    }

    private static Task<HttpResponseMessage> GameGetAsync(HttpClient client, string host,
        string cookie) => client.SendAsync(GameRequest(HttpMethod.Get, host, cookie));

    private static Task<HttpResponseMessage> RedeemAsync(HttpClient client, string host,
        string ticket)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/__session")
        {
            Content = JsonContent.Create(new { ticket })
        };
        request.Headers.Host = host;
        return client.SendAsync(request);
    }
}
