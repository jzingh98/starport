using System.Net;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Starport.Api.Storage;
using Starport.Domain.Access;
using Starport.Infrastructure.Accounts;
using Starport.Infrastructure.Database;

namespace Starport.Tests.Access;

public sealed class AccountsServingLossConsumerTests
{
    [Fact]
    public async Task Concurrent_consumers_unpublish_once_and_advance_the_cursor_atomically()
    {
        var server = Environment.GetEnvironmentVariable("STARPORT_TEST_DATABASE");
        if (string.IsNullOrWhiteSpace(server)) return;
        await using var database = await TemporaryDatabase.CreateAsync(server);
        var services = new ServiceCollection()
            .AddDbContext<StarportDbContext>(o => o.UseNpgsql(database.ConnectionString))
            .BuildServiceProvider();
        await using (var scope = services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<StarportDbContext>();
            await db.Database.MigrateAsync();
            db.Workspaces.Add(new WorkspaceRecord
            {
                Id = "ws_creator", Slug = "creator", Name = "Creator",
                Kind = WorkspaceKind.Personal, CreatedAt = DateTimeOffset.UtcNow
            });
            db.Games.Add(new GameRecord
            {
                Id = Guid.NewGuid(), WorkspaceId = "ws_creator", Slug = "event-game", Title = "Event game",
                Description = "Test", Audience = GameAudience.People, IsPublished = true,
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
        }

        var handler = new ReplyHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/token"))
                return Json("{\"access_token\":\"service-token\",\"expires_in\":300}");
            Assert.Equal("Bearer service-token", request.Headers.Authorization?.ToString());
            Assert.Equal("/v1/entitlements/events", request.RequestUri.AbsolutePath);
            return request.RequestUri.Query.Contains("after=0", StringComparison.Ordinal)
                ? Json("{\"events\":[{\"id\":1,\"workspaceId\":\"ws_creator\",\"product\":\"starport\",\"kind\":\"serving_lost\",\"effectiveAt\":\"2026-11-02T12:00:00Z\"}],\"nextCursor\":1}")
                : Json("{\"events\":[],\"nextCursor\":1}");
        });
        var factory = new ClientFactory(handler);
        var options = Options.Create(new AccountsAccessOptions
        {
            BaseUrl = "https://accounts.example/", TokenUrl = "https://identity.example/token",
            ClientId = "starport-service", ClientSecret = "test-secret"
        });
        var token = new AccountsServiceTokenProvider(factory, options, TimeProvider.System);
        var consumer = new AccountsServingLossConsumer(
            services.GetRequiredService<IServiceScopeFactory>(), factory, token, options,
            TimeProvider.System, NullLogger<AccountsServingLossConsumer>.Instance);

        var results = await Task.WhenAll(consumer.ConsumeOnceAsync(), consumer.ConsumeOnceAsync());
        Assert.Equal([0, 1], results.Order());
        await using (var scope = services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<StarportDbContext>();
            Assert.False((await db.Games.SingleAsync()).IsPublished);
            Assert.Equal(1, (await db.AccountsEventCursors.SingleAsync()).LastEventId);
            // A repeated empty poll does not change a newly published game.
            await db.Games.ExecuteUpdateAsync(setters => setters.SetProperty(g => g.IsPublished, true));
        }
        Assert.Equal(0, await consumer.ConsumeOnceAsync());
        await using (var scope = services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<StarportDbContext>();
            Assert.True((await db.Games.SingleAsync()).IsPublished);
            Assert.Equal(1, (await db.AccountsEventCursors.SingleAsync()).LastEventId);
        }
        await services.DisposeAsync();
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

    private sealed class TemporaryDatabase(string adminConnection, string name) : IAsyncDisposable
    {
        public string ConnectionString { get; } = new NpgsqlConnectionStringBuilder(adminConnection)
        {
            Database = name
        }.ConnectionString;

        public static async Task<TemporaryDatabase> CreateAsync(string connection)
        {
            var name = "starport_test_" + Guid.NewGuid().ToString("N")[..16];
            var admin = new NpgsqlConnectionStringBuilder(connection) { Database = "postgres" };
            await using var db = new NpgsqlConnection(admin.ConnectionString);
            await db.OpenAsync();
            await using var command = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", db);
            await command.ExecuteNonQueryAsync();
            return new TemporaryDatabase(connection, name);
        }

        public async ValueTask DisposeAsync()
        {
            NpgsqlConnection.ClearAllPools();
            var admin = new NpgsqlConnectionStringBuilder(ConnectionString) { Database = "postgres" };
            await using var db = new NpgsqlConnection(admin.ConnectionString);
            await db.OpenAsync();
            await using var command = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE)", db);
            await command.ExecuteNonQueryAsync();
        }
    }
}
