using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Starport.Api.Storage;
using Starport.Domain.Access;
using Starport.Infrastructure.Database;

namespace Starport.Tests.Access;

public sealed class PublishingQuotaPostgresTests
{
    [Fact]
    public async Task Delivery_meter_uses_one_utc_timestamp_at_month_rollover()
    {
        var fixture = await QuotaFixture.CreateAsync("{}");
        if (fixture is null) return;
        await using (fixture)
        {
            var december = new DateTimeOffset(2026, 12, 31, 23, 59, 59, TimeSpan.Zero);
            var january = december.AddSeconds(2);
            var meter = new DeliveryUsageMeter(new SteppingClock(december, january));
            Assert.True(await meter.TryReserveAsync(fixture.Db, fixture.WorkspaceId,
                1024, 1, CancellationToken.None));
            var usage = await fixture.Db.WorkspaceDeliveryUsage.AsNoTracking().SingleAsync(u =>
                u.WorkspaceId == fixture.WorkspaceId);
            Assert.Equal(new DateOnly(2026, 12, 1), usage.Month);
            Assert.Equal(december, usage.UpdatedAt);
        }
    }

    [Fact]
    public async Task Catalog_hides_games_at_monthly_cap_and_usage_is_private_to_publishers()
    {
        var fixture = await QuotaFixture.CreateAsync(
            "{\"games\":10,\"monthly_delivery_mb\":1,\"retained_bytes_mb\":2}");
        if (fixture is null) return;
        await using (fixture)
        {
            var now = DateTimeOffset.UtcNow;
            var gameId = Guid.NewGuid();
            var releaseId = Guid.NewGuid();
            AddGame(fixture.Db, gameId, releaseId, fixture.WorkspaceId, now);
            var game = fixture.Db.Games.Local.Single(g => g.Id == gameId);
            game.Audience = GameAudience.Public;
            game.IsPublished = true;
            game.MetadataRevision = 1;
            AddRelease(fixture.Db, gameId, releaseId, 500 * 1024, now);
            fixture.Db.GameReviewApprovals.Add(new GameReviewApprovalRecord
            {
                Id = Guid.NewGuid(), GameId = gameId, ReleaseId = releaseId,
                ReleaseSha256 = new string('a', 64), MetadataRevision = 1,
                ReviewerSubject = "moderator", ApprovedAt = now
            });
            await fixture.Db.SaveChangesAsync();

            var catalog = await fixture.Client.GetFromJsonAsync<GameCard[]>("/api/games");
            Assert.Contains(catalog!, item => item.Id == gameId);
            Assert.Equal(HttpStatusCode.OK,
                (await fixture.Client.GetAsync($"/api/games/{gameId}")).StatusCode);

            var month = new DateOnly(now.UtcDateTime.Year, now.UtcDateTime.Month, 1);
            fixture.Db.WorkspaceDeliveryUsage.Add(new WorkspaceDeliveryUsageRecord
            {
                WorkspaceId = fixture.WorkspaceId, Month = month, Bytes = 1024 * 1024,
                UpdatedAt = now
            });
            await fixture.Db.SaveChangesAsync();

            var usageResponse = await fixture.Client.GetAsync(
                $"/api/workspaces/{fixture.WorkspaceId}/usage");
            Assert.Equal(HttpStatusCode.OK, usageResponse.StatusCode);
            using var usage = JsonDocument.Parse(await usageResponse.Content.ReadAsStringAsync());
            Assert.Equal(1, usage.RootElement.GetProperty("games").GetInt32());
            Assert.Equal(1, usage.RootElement.GetProperty("publishedGames").GetInt32());
            Assert.Equal(500 * 1024, usage.RootElement.GetProperty("retainedBytes").GetInt64());
            Assert.Equal(1024 * 1024,
                usage.RootElement.GetProperty("deliveredBytesThisMonth").GetInt64());
            Assert.Equal(1, usage.RootElement.GetProperty("limits")
                .GetProperty("monthly_delivery_mb").GetInt32());
            Assert.Equal("no-store", usageResponse.Headers.CacheControl?.ToString());

            catalog = await fixture.Client.GetFromJsonAsync<GameCard[]>("/api/games");
            Assert.DoesNotContain(catalog!, item => item.Id == gameId);
            Assert.Equal(HttpStatusCode.TooManyRequests,
                (await fixture.Client.GetAsync($"/api/games/{gameId}")).StatusCode);
            Assert.Equal(HttpStatusCode.TooManyRequests,
                (await fixture.Client.PostAsync($"/api/games/{gameId}/launch", null)).StatusCode);

            await fixture.Db.WorkspaceMembers.Where(m => m.WorkspaceId == fixture.WorkspaceId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(m => m.Role, WorkspaceRole.Viewer));
            Assert.Equal(HttpStatusCode.NotFound,
                (await fixture.Client.GetAsync($"/api/workspaces/{fixture.WorkspaceId}/usage")).StatusCode);
        }
    }

    private sealed record GameCard(Guid Id);

    [Fact]
    public async Task Concurrent_delivery_reservations_never_exceed_the_monthly_cap()
    {
        var fixture = await QuotaFixture.CreateAsync("{}");
        if (fixture is null) return;
        await using (fixture)
        {
            var connection = Environment.GetEnvironmentVariable("STARPORT_TEST_DATABASE")!;
            var now = new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
            var meter = new DeliveryUsageMeter(new FixedClock(now));
            Assert.False(await meter.HasRemainingAsync(fixture.Db, fixture.WorkspaceId,
                0, CancellationToken.None));
            var calls = Enumerable.Range(0, 8).Select(async _ =>
            {
                await using var db = new StarportDbContext(
                    new DbContextOptionsBuilder<StarportDbContext>().UseNpgsql(connection).Options);
                return await meter.TryReserveAsync(db, fixture.WorkspaceId,
                    200 * 1024, 1, CancellationToken.None);
            }).ToArray();
            var accepted = await Task.WhenAll(calls);
            Assert.Equal(5, accepted.Count(value => value));
            var usage = await fixture.Db.WorkspaceDeliveryUsage.AsNoTracking().SingleAsync(u =>
                u.WorkspaceId == fixture.WorkspaceId && u.Month == new DateOnly(2026, 11, 1));
            Assert.Equal(1000 * 1024, usage.Bytes);
            Assert.True(await meter.TryReserveAsync(fixture.Db, fixture.WorkspaceId,
                24 * 1024, 1, CancellationToken.None));
            Assert.False(await meter.TryReserveAsync(fixture.Db, fixture.WorkspaceId,
                1, 1, CancellationToken.None));
            Assert.False(await meter.HasRemainingAsync(fixture.Db, fixture.WorkspaceId,
                1, CancellationToken.None));
        }
    }

    [Fact]
    public async Task Concurrent_drafts_cannot_exceed_the_workspace_game_limit()
    {
        var fixture = await QuotaFixture.CreateAsync("{\"games\":1}");
        if (fixture is null) return;
        await using (fixture)
        {
            var calls = Enumerable.Range(0, 8).Select(index => fixture.Client.PostAsJsonAsync(
                $"/api/workspaces/{fixture.WorkspaceId}/games",
                new { slug = $"quota-{index}-{Guid.NewGuid():N}", title = "Quota game",
                    description = "Concurrent creation" })).ToArray();
            var results = await Task.WhenAll(calls);
            Assert.Single(results, response => response.StatusCode == HttpStatusCode.Created);
            Assert.All(results, response => Assert.Contains(response.StatusCode,
                new[] { HttpStatusCode.Created, HttpStatusCode.PaymentRequired }));
            Assert.Equal(1, await fixture.Db.Games.CountAsync(g =>
                g.WorkspaceId == fixture.WorkspaceId));
        }
    }

    [Fact]
    public async Task Concurrent_publications_return_one_success_and_one_quota_response()
    {
        var fixture = await QuotaFixture.CreateAsync(
            "{\"games\":10,\"published_games\":1}");
        if (fixture is null) return;
        await using (fixture)
        {
            var now = DateTimeOffset.UtcNow;
            var gameOne = Guid.NewGuid();
            var gameTwo = Guid.NewGuid();
            var releaseOne = Guid.NewGuid();
            var releaseTwo = Guid.NewGuid();
            AddGame(fixture.Db, gameOne, releaseOne, fixture.WorkspaceId, now);
            AddGame(fixture.Db, gameTwo, releaseTwo, fixture.WorkspaceId, now);
            AddRelease(fixture.Db, gameOne, releaseOne, 1024, now);
            AddRelease(fixture.Db, gameTwo, releaseTwo, 1024, now);
            await fixture.Db.SaveChangesAsync();

            var responses = await Task.WhenAll(
                fixture.Client.PutAsJsonAsync(
                    $"/api/workspaces/{fixture.WorkspaceId}/games/{gameOne}/publication",
                    new { published = true }),
                fixture.Client.PutAsJsonAsync(
                    $"/api/workspaces/{fixture.WorkspaceId}/games/{gameTwo}/publication",
                    new { published = true }));
            Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK);
            Assert.Single(responses, response => response.StatusCode == HttpStatusCode.PaymentRequired);
            Assert.Equal(1, await fixture.Db.Games.AsNoTracking().CountAsync(g =>
                g.WorkspaceId == fixture.WorkspaceId && g.IsPublished));
        }
    }

    [Fact]
    public async Task Publication_cap_and_retained_bytes_drop_only_after_inactive_release_cleanup()
    {
        var fixture = await QuotaFixture.CreateAsync(
            "{\"games\":10,\"published_games\":1,\"retained_bytes_mb\":1}");
        if (fixture is null) return;
        await using (fixture)
        {
            var now = DateTimeOffset.UtcNow;
            var gameOne = Guid.NewGuid();
            var gameTwo = Guid.NewGuid();
            var activeOne = Guid.NewGuid();
            var activeTwo = Guid.NewGuid();
            var inactive = Guid.NewGuid();
            AddGame(fixture.Db, gameOne, activeOne, fixture.WorkspaceId, now);
            AddGame(fixture.Db, gameTwo, activeTwo, fixture.WorkspaceId, now);
            AddRelease(fixture.Db, gameOne, activeOne, 100 * 1024, now);
            AddRelease(fixture.Db, gameTwo, activeTwo, 100 * 1024, now);
            AddRelease(fixture.Db, gameOne, inactive, 700 * 1024, now);
            await fixture.Db.SaveChangesAsync();
            var inactiveDirectory = Path.Combine(fixture.Scratch, "releases", inactive.ToString("N"));
            Directory.CreateDirectory(inactiveDirectory);
            await File.WriteAllTextAsync(Path.Combine(inactiveDirectory, "index.html"), "old file");

            var publishOne = await fixture.Client.PutAsJsonAsync(
                $"/api/workspaces/{fixture.WorkspaceId}/games/{gameOne}/publication",
                new { published = true });
            Assert.Equal(HttpStatusCode.OK, publishOne.StatusCode);
            var publishTwo = await fixture.Client.PutAsJsonAsync(
                $"/api/workspaces/{fixture.WorkspaceId}/games/{gameTwo}/publication",
                new { published = true });
            Assert.Equal(HttpStatusCode.PaymentRequired, publishTwo.StatusCode);
            Assert.True((await fixture.Db.Games.AsNoTracking().SingleAsync(g => g.Id == gameOne)).IsPublished);
            Assert.False((await fixture.Db.Games.AsNoTracking().SingleAsync(g => g.Id == gameTwo)).IsPublished);

            await RetainedStorageQuota.EnsureFitsAsync(fixture.Db, fixture.WorkspaceId,
                100 * 1024, 1, CancellationToken.None);
            await Assert.ThrowsAsync<RetainedQuotaExceededException>(() =>
                RetainedStorageQuota.EnsureFitsAsync(fixture.Db, fixture.WorkspaceId,
                    200 * 1024, 1, CancellationToken.None));

            var activeDelete = await fixture.Client.DeleteAsync(
                $"/api/workspaces/{fixture.WorkspaceId}/games/{gameOne}/releases/{activeOne}");
            Assert.Equal(HttpStatusCode.Conflict, activeDelete.StatusCode);
            var delete = await fixture.Client.DeleteAsync(
                $"/api/workspaces/{fixture.WorkspaceId}/games/{gameOne}/releases/{inactive}");
            Assert.Equal(HttpStatusCode.OK, delete.StatusCode);
            Assert.False((await fixture.Db.GameReleases.AsNoTracking().SingleAsync(r =>
                r.Id == inactive)).IsValidated);
            await Assert.ThrowsAsync<RetainedQuotaExceededException>(() =>
                RetainedStorageQuota.EnsureFitsAsync(fixture.Db, fixture.WorkspaceId,
                    200 * 1024, 1, CancellationToken.None));

            var cleanup = new ReleaseCleanupWorker(
                fixture.Factory.Services.GetRequiredService<IServiceScopeFactory>(),
                fixture.Factory.Services.GetRequiredService<IConfiguration>(),
                NullLogger<ReleaseCleanupWorker>.Instance);
            await cleanup.StartAsync(CancellationToken.None);
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                while (await fixture.Db.ReleaseFiles.AsNoTracking().AnyAsync(f =>
                    f.ReleaseId == inactive, timeout.Token))
                    await Task.Delay(50, timeout.Token);
            }
            finally { await cleanup.StopAsync(CancellationToken.None); }
            Assert.False(Directory.Exists(inactiveDirectory));
            await RetainedStorageQuota.EnsureFitsAsync(fixture.Db, fixture.WorkspaceId,
                800 * 1024, 1, CancellationToken.None);
        }
    }

    private static void AddGame(StarportDbContext db, Guid id, Guid activeRelease,
        string workspaceId, DateTimeOffset now) => db.Games.Add(new GameRecord
        {
            Id = id, WorkspaceId = workspaceId, Slug = "quota-" + id.ToString("N"),
            Title = "Quota game", Description = "Test", Audience = GameAudience.Team,
            IsPublished = false, ActiveReleaseId = activeRelease,
            CreatedAt = now, UpdatedAt = now
        });

    private static void AddRelease(StarportDbContext db, Guid gameId, Guid releaseId,
        long bytes, DateTimeOffset now)
    {
        db.GameReleases.Add(new GameReleaseRecord
        {
            Id = releaseId, GameId = gameId, Sha256 = new string('a', 64),
            StorageKey = "releases/" + releaseId.ToString("N"), IsValidated = true,
            CreatedAt = now, ValidatedAt = now
        });
        db.ReleaseFiles.Add(new ReleaseFileRecord
        {
            ReleaseId = releaseId, Path = "index.html", Bytes = bytes,
            Sha256 = new string('b', 64)
        });
    }

    private sealed class QuotaFixture : IAsyncDisposable
    {
        public required WebApplicationFactory<Program> Factory { get; init; }
        public required HttpClient Client { get; init; }
        public required IServiceScope Scope { get; init; }
        public required StarportDbContext Db { get; init; }
        public required string WorkspaceId { get; init; }
        public required string Scratch { get; init; }

        public static async Task<QuotaFixture?> CreateAsync(string limitsJson)
        {
            var connection = Environment.GetEnvironmentVariable("STARPORT_TEST_DATABASE");
            if (string.IsNullOrWhiteSpace(connection)) return null;
            await using (var migration = new StarportDbContext(
                new DbContextOptionsBuilder<StarportDbContext>().UseNpgsql(connection).Options))
                await migration.Database.MigrateAsync();
            var scratch = Path.Combine(Path.GetTempPath(), "starport-quota-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(scratch);
            var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Development");
                builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["ConnectionStrings:Database"] = connection,
                        ["Database:Migrate"] = "skip",
                        ["Auth:Mode"] = "development",
                        ["Auth:Development:Subject"] = "quota-publisher",
                        ["Workspaces:Source"] = "local",
                        ["Storage:Mode"] = "local",
                        ["Storage:Root"] = scratch,
                        ["Processing:Mode"] = "none",
                        ["Reconciliation:Enabled"] = "false",
                        ["GameDelivery:Enabled"] = "true",
                        ["GameDelivery:RootDomain"] = "localhost",
                        ["GameDelivery:Scheme"] = "http",
                        ["GameDelivery:Port"] = "80",
                        ["GameDelivery:AppOrigin"] = "http://localhost"
                    }));
                builder.ConfigureServices(services => services.AddSingleton<StoragePaths>());
            });
            var client = factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false, HandleCookies = false
            });
            var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<StarportDbContext>();
            Assert.Equal(new Npgsql.NpgsqlConnectionStringBuilder(connection).Database,
                db.Database.GetDbConnection().Database);
            var workspaceId = "ws_" + Guid.NewGuid().ToString("N");
            var now = DateTimeOffset.UtcNow;
            db.Workspaces.Add(new WorkspaceRecord
            {
                Id = workspaceId, Slug = workspaceId, Name = "Quota test",
                Kind = WorkspaceKind.Personal, CreatedAt = now
            });
            db.WorkspaceMembers.Add(new WorkspaceMemberRecord
            {
                WorkspaceId = workspaceId, Subject = "quota-publisher", Role = WorkspaceRole.Owner
            });
            db.WorkspacePublishing.Add(new WorkspacePublishingRecord
            {
                WorkspaceId = workspaceId, CanPublish = true,
                PaidThrough = now.AddHours(2), LimitsJson = limitsJson
            });
            await db.SaveChangesAsync();
            return new QuotaFixture
            {
                Factory = factory, Client = client, Scope = scope, Db = db,
                WorkspaceId = workspaceId, Scratch = scratch
            };
        }

        public async ValueTask DisposeAsync()
        {
            Db.ChangeTracker.Clear();
            await Db.Games.Where(g => g.WorkspaceId == WorkspaceId).ExecuteDeleteAsync();
            await Db.WorkspaceDeliveryUsage.Where(u => u.WorkspaceId == WorkspaceId)
                .ExecuteDeleteAsync();
            await Db.Workspaces.Where(w => w.Id == WorkspaceId).ExecuteDeleteAsync();
            Scope.Dispose();
            Client.Dispose();
            Factory.Dispose();
            Directory.Delete(Scratch, recursive: true);
        }
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class SteppingClock(DateTimeOffset first, DateTimeOffset later) : TimeProvider
    {
        private int calls;
        public override DateTimeOffset GetUtcNow() =>
            Interlocked.Increment(ref calls) == 1 ? first : later;
    }
}
