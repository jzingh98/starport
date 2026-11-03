using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Starport.Domain.Access;
using Starport.Infrastructure.Database;

namespace Starport.Tests.Access;

public sealed class PlayerApiPostgresTests
{
    [Fact]
    public async Task Launch_is_one_use_and_game_host_and_saves_recheck_live_access()
    {
        var connection = Environment.GetEnvironmentVariable("STARPORT_TEST_DATABASE");
        if (string.IsNullOrWhiteSpace(connection)) return;
        await using (var migration = new StarportDbContext(
            new DbContextOptionsBuilder<StarportDbContext>().UseNpgsql(connection).Options))
            await migration.Database.MigrateAsync();

        var scratch = Path.Combine(Path.GetTempPath(), "starport-player-test-" + Guid.NewGuid().ToString("N"));
        var gameId = Guid.NewGuid();
        var releaseId = Guid.NewGuid();
        var workspaceId = "ws_" + Guid.NewGuid().ToString("N");
        var file = "<html><body>isolated game</body></html>";
        var releaseDir = Path.Combine(scratch, "releases", releaseId.ToString("N"));
        Directory.CreateDirectory(releaseDir);
        await File.WriteAllTextAsync(Path.Combine(releaseDir, "index.html"), file);

        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Database"] = connection,
                    ["Database:Migrate"] = "skip",
                    ["Auth:Mode"] = "development",
                    ["Auth:Development:Subject"] = "player-1",
                    ["Workspaces:Source"] = "local",
                    ["Storage:Mode"] = "local",
                    ["Storage:Root"] = scratch,
                    ["Processing:Mode"] = "none",
                    ["Reconciliation:Enabled"] = "false",
                    ["GameDelivery:RootDomain"] = "localhost",
                    ["GameDelivery:Enabled"] = "true",
                    ["GameDelivery:Scheme"] = "http",
                    ["GameDelivery:Port"] = "80",
                    ["GameDelivery:AppOrigin"] = "http://localhost"
                }));
            builder.ConfigureServices(services => services.AddSingleton<Starport.Api.Storage.StoragePaths>());
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
            Id = workspaceId, Slug = workspaceId, Name = "Player API test",
            Kind = WorkspaceKind.Personal, CreatedAt = now
        });
        db.WorkspacePublishing.Add(new WorkspacePublishingRecord
        {
            WorkspaceId = workspaceId, CanPublish = true,
            PaidThrough = now.AddHours(2), LimitsJson = "{}"
        });
        db.Games.Add(new GameRecord
        {
            Id = gameId, WorkspaceId = workspaceId,
            Slug = "player-" + gameId.ToString("N"),
            Title = "Player API test", Description = "Test",
            Audience = GameAudience.Public, IsPublished = true,
            ActiveReleaseId = releaseId, MetadataRevision = 1,
            CreatedAt = now, UpdatedAt = now
        });
        var archiveSha = new string('a', 64);
        db.GameReleases.Add(new GameReleaseRecord
        {
            Id = releaseId, GameId = gameId, Sha256 = archiveSha,
            StorageKey = "releases/" + releaseId.ToString("N"),
            IsValidated = true, CreatedAt = now, ValidatedAt = now
        });
        db.ReleaseFiles.Add(new ReleaseFileRecord
        {
            ReleaseId = releaseId, Path = "index.html",
            Bytes = Encoding.UTF8.GetByteCount(file),
            Sha256 = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(file)))
        });
        db.GameReviewApprovals.Add(new GameReviewApprovalRecord
        {
            Id = Guid.NewGuid(), GameId = gameId, ReleaseId = releaseId,
            ReleaseSha256 = archiveSha, MetadataRevision = 1,
            ReviewerSubject = "moderator", ApprovedAt = now
        });
        await db.SaveChangesAsync();

        try
        {
            var gameHost = $"g-{gameId:N}.localhost";
            var noCookie = await GameGetAsync(client, gameHost, "/index.html");
            Assert.Equal(HttpStatusCode.NotFound, noCookie.StatusCode);

            var launch = await client.PostAsync($"/api/games/{gameId}/launch", null);
            Assert.True(launch.StatusCode == HttpStatusCode.OK,
                await launch.Content.ReadAsStringAsync());
            using var launchBody = JsonDocument.Parse(await launch.Content.ReadAsStringAsync());
            var embed = launchBody.RootElement.GetProperty("embedUrl").GetString()!;
            Assert.StartsWith($"http://{gameHost}:80/__launch#ticket=", embed);
            var ticket = embed.Split("#ticket=", 2)[1];
            var redeem = await RedeemAsync(client, gameHost, ticket);
            Assert.Equal(HttpStatusCode.NoContent, redeem.StatusCode);
            var cookie = redeem.Headers.GetValues("Set-Cookie").Single().Split(';')[0];
            Assert.StartsWith("starport-game=", cookie);
            Assert.Equal(HttpStatusCode.NotFound, (await RedeemAsync(client, gameHost, ticket)).StatusCode);

            var otherHost = $"g-{Guid.NewGuid():N}.localhost";
            Assert.Equal(HttpStatusCode.NotFound,
                (await GameGetAsync(client, otherHost, "/index.html", cookie)).StatusCode);
            var asset = await GameGetAsync(client, gameHost, "/index.html", cookie);
            Assert.Equal(HttpStatusCode.OK, asset.StatusCode);
            Assert.Equal(file, await asset.Content.ReadAsStringAsync());
            Assert.Contains("frame-ancestors http://localhost", asset.Headers.GetValues("Content-Security-Policy").Single());
            Assert.Contains("form-action 'none'", asset.Headers.GetValues("Content-Security-Policy").Single());
            var delivered = await db.WorkspaceDeliveryUsage.AsNoTracking().SingleAsync(u =>
                u.WorkspaceId == workspaceId);
            Assert.Equal(Encoding.UTF8.GetByteCount(file), delivered.Bytes);
            var head = new HttpRequestMessage(HttpMethod.Head, "/index.html");
            head.Headers.Host = gameHost;
            head.Headers.TryAddWithoutValidation("Cookie", cookie);
            Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(head)).StatusCode);
            Assert.Equal(delivered.Bytes, (await db.WorkspaceDeliveryUsage.AsNoTracking()
                .SingleAsync(u => u.WorkspaceId == workspaceId)).Bytes);

            var create = new HttpRequestMessage(HttpMethod.Put, $"/api/games/{gameId}/saves/main")
            {
                Content = JsonContent.Create(new { data = new { level = 1 }, requestKey = "create-main" })
            };
            create.Headers.TryAddWithoutValidation("If-None-Match", "*");
            var created = await client.SendAsync(create);
            Assert.Equal(HttpStatusCode.OK, created.StatusCode);
            var revision = created.Headers.ETag!.Tag;

            var createReplay = new HttpRequestMessage(HttpMethod.Put, $"/api/games/{gameId}/saves/main")
            {
                Content = JsonContent.Create(new { data = new { level = 1 }, requestKey = "create-main" })
            };
            createReplay.Headers.TryAddWithoutValidation("If-None-Match", "*");
            var replayedCreate = await client.SendAsync(createReplay);
            Assert.Equal(HttpStatusCode.OK, replayedCreate.StatusCode);
            Assert.Equal(revision, replayedCreate.Headers.ETag!.Tag);

            var reusedCreateKey = new HttpRequestMessage(HttpMethod.Put, $"/api/games/{gameId}/saves/main")
            {
                Content = JsonContent.Create(new { data = new { level = 99 }, requestKey = "create-main" })
            };
            reusedCreateKey.Headers.TryAddWithoutValidation("If-None-Match", "*");
            Assert.Equal(HttpStatusCode.Conflict,
                (await client.SendAsync(reusedCreateKey)).StatusCode);

            var saves = await client.GetAsync($"/api/games/{gameId}/saves");
            Assert.Equal(HttpStatusCode.OK, saves.StatusCode);
            using var savesBody = JsonDocument.Parse(await saves.Content.ReadAsStringAsync());
            Assert.Single(savesBody.RootElement.EnumerateArray());
            Assert.Equal("main", savesBody.RootElement[0].GetProperty("slot").GetString());

            var save = await client.GetAsync($"/api/games/{gameId}/saves/main");
            Assert.Equal(HttpStatusCode.OK, save.StatusCode);
            Assert.Equal(revision, save.Headers.ETag!.Tag);
            using var saveBody = JsonDocument.Parse(await save.Content.ReadAsStringAsync());
            Assert.Equal(1, saveBody.RootElement.GetProperty("data").GetProperty("level").GetInt32());

            var update = new HttpRequestMessage(HttpMethod.Put, $"/api/games/{gameId}/saves/main")
            {
                Content = JsonContent.Create(new { data = new { level = 2 }, requestKey = "update-main" })
            };
            update.Headers.TryAddWithoutValidation("If-Match", revision);
            var updated = await client.SendAsync(update);
            Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
            Assert.NotEqual(revision, updated.Headers.ETag!.Tag);

            var updateReplay = new HttpRequestMessage(HttpMethod.Put, $"/api/games/{gameId}/saves/main")
            {
                Content = JsonContent.Create(new { data = new { level = 2 }, requestKey = "update-main" })
            };
            updateReplay.Headers.TryAddWithoutValidation("If-Match", revision);
            var replayedUpdate = await client.SendAsync(updateReplay);
            Assert.Equal(HttpStatusCode.OK, replayedUpdate.StatusCode);
            Assert.Equal(updated.Headers.ETag!.Tag, replayedUpdate.Headers.ETag!.Tag);

            var stale = new HttpRequestMessage(HttpMethod.Put, $"/api/games/{gameId}/saves/main")
            {
                Content = JsonContent.Create(new { data = new { level = 3 } })
            };
            stale.Headers.TryAddWithoutValidation("If-Match", revision);
            Assert.Equal(HttpStatusCode.PreconditionFailed, (await client.SendAsync(stale)).StatusCode);

            var staleDelete = new HttpRequestMessage(HttpMethod.Delete, $"/api/games/{gameId}/saves/main");
            staleDelete.Headers.TryAddWithoutValidation("If-Match", revision);
            Assert.Equal(HttpStatusCode.PreconditionFailed,
                (await client.SendAsync(staleDelete)).StatusCode);

            var publishing = await db.WorkspacePublishing.SingleAsync(p => p.WorkspaceId == workspaceId);
            publishing.PaidThrough = DateTimeOffset.UtcNow.AddSeconds(-1);
            await db.SaveChangesAsync();
            Assert.Equal(HttpStatusCode.NotFound,
                (await GameGetAsync(client, gameHost, "/index.html", cookie)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound,
                (await client.GetAsync($"/api/games/{gameId}/saves/main")).StatusCode);

            var offlineList = await client.GetAsync("/api/me/saves");
            Assert.Equal(HttpStatusCode.OK, offlineList.StatusCode);
            using (var offlineListBody = JsonDocument.Parse(await offlineList.Content.ReadAsStringAsync()))
            {
                var rows = offlineListBody.RootElement.EnumerateArray().ToArray();
                Assert.Single(rows);
                Assert.Equal(gameId, rows[0].GetProperty("gameId").GetGuid());
                Assert.Equal("main", rows[0].GetProperty("slot").GetString());
            }
            var offlineSave = await client.GetAsync($"/api/me/saves/{gameId}/main");
            Assert.Equal(HttpStatusCode.OK, offlineSave.StatusCode);
            Assert.Equal(updated.Headers.ETag!.Tag, offlineSave.Headers.ETag!.Tag);
            using (var offlineSaveBody = JsonDocument.Parse(await offlineSave.Content.ReadAsStringAsync()))
                Assert.Equal(2, offlineSaveBody.RootElement.GetProperty("data")
                    .GetProperty("level").GetInt32());

            var export = await client.GetAsync("/api/me/saves/export");
            Assert.Equal(HttpStatusCode.OK, export.StatusCode);
            Assert.Equal("attachment", export.Content.Headers.ContentDisposition?.DispositionType);
            using (var exportBody = JsonDocument.Parse(await export.Content.ReadAsStringAsync()))
            {
                var rows = exportBody.RootElement.GetProperty("saves").EnumerateArray().ToArray();
                Assert.Single(rows);
                Assert.Equal(gameId, rows[0].GetProperty("gameId").GetGuid());
                Assert.Equal(2, rows[0].GetProperty("data").GetProperty("level").GetInt32());
            }
            var staleAccountDelete = new HttpRequestMessage(HttpMethod.Delete,
                $"/api/me/saves/{gameId}/main");
            staleAccountDelete.Headers.TryAddWithoutValidation("If-Match", revision);
            Assert.Equal(HttpStatusCode.PreconditionFailed,
                (await client.SendAsync(staleAccountDelete)).StatusCode);
            var accountDelete = new HttpRequestMessage(HttpMethod.Delete,
                $"/api/me/saves/{gameId}/main");
            accountDelete.Headers.TryAddWithoutValidation("If-Match", updated.Headers.ETag!.Tag);
            Assert.Equal(HttpStatusCode.NoContent,
                (await client.SendAsync(accountDelete)).StatusCode);
            var deletedList = await client.GetAsync("/api/me/saves");
            using (var deletedBody = JsonDocument.Parse(await deletedList.Content.ReadAsStringAsync()))
                Assert.Empty(deletedBody.RootElement.EnumerateArray());
        }
        finally
        {
            db.ChangeTracker.Clear();
            await db.Games.Where(g => g.Id == gameId).ExecuteDeleteAsync();
            await db.WorkspaceDeliveryUsage.Where(u => u.WorkspaceId == workspaceId)
                .ExecuteDeleteAsync();
            await db.Workspaces.Where(w => w.Id == workspaceId).ExecuteDeleteAsync();
            Directory.Delete(scratch, recursive: true);
        }
    }

    private static Task<HttpResponseMessage> GameGetAsync(HttpClient client, string host,
        string path, string? cookie = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Host = host;
        if (cookie is not null) request.Headers.TryAddWithoutValidation("Cookie", cookie);
        return client.SendAsync(request);
    }

    private static Task<HttpResponseMessage> RedeemAsync(HttpClient client, string host, string ticket)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/__session")
        {
            Content = JsonContent.Create(new { ticket })
        };
        request.Headers.Host = host;
        return client.SendAsync(request);
    }
}
