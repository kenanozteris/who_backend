using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Who.Infrastructure.Persistence;

namespace Who.IntegrationTests;

public sealed class FoundationTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task Real_postgres_18_connects_through_EF_and_both_health_endpoints_succeed()
    {
        using var factory = new ApiFactory(postgres.Container.GetConnectionString());
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/ready")).StatusCode);
        using var scope = factory.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<WhoDbContext>();
        Assert.Equal("Npgsql.EntityFrameworkCore.PostgreSQL", database.Database.ProviderName);
        Assert.Empty(database.Model.GetEntityTypes());
        var connection = database.Database.GetDbConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand(); command.CommandText = "SHOW server_version";
        Assert.StartsWith("18.", (string)(await command.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task Readiness_fails_during_postgres_outage_but_liveness_survives_and_readiness_recovers()
    {
        using var factory = new ApiFactory(postgres.Container.GetConnectionString());
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/ready")).StatusCode);
        await postgres.Container.StopAsync();
        try
        {
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/health/ready")).StatusCode);
        }
        finally { await postgres.Container.StartAsync(); }
        // Docker reallocates the random published port after stop/start.
        // Rebind the test host to the new endpoint; Compose uses a stable port.
        using var recoveredFactory = new ApiFactory(postgres.Container.GetConnectionString());
        using var recoveredClient = recoveredFactory.CreateClient();
        // A restarted process can accept TCP before PostgreSQL is ready for queries.
        var recovery = System.Diagnostics.Stopwatch.StartNew();
        HttpStatusCode recoveredStatus;
        do
        {
            recoveredStatus = (await recoveredClient.GetAsync("/health/ready")).StatusCode;
            if (recoveredStatus == HttpStatusCode.OK) break;
            await Task.Delay(TimeSpan.FromMilliseconds(250));
        } while (recovery.Elapsed < TimeSpan.FromSeconds(20));
        Assert.Equal(HttpStatusCode.OK, recoveredStatus);
    }

    [Fact]
    public async Task OpenApi_is_development_only_and_missing_api_routes_use_ProblemDetails()
    {
        using var development = new ApiFactory(postgres.Container.GetConnectionString());
        using var devClient = development.CreateClient();
        using var document = JsonDocument.Parse(await devClient.GetStringAsync("/openapi/v1.json"));
        Assert.True(document.RootElement.TryGetProperty("openapi", out _));
        using var production = new ApiFactory(postgres.Container.GetConnectionString(), "Production");
        using var prodClient = production.CreateClient();
        Assert.Equal(HttpStatusCode.NotFound, (await prodClient.GetAsync("/openapi/v1.json")).StatusCode);
        var response = await prodClient.GetAsync("/api/v1/not-implemented");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(404, problem.RootElement.GetProperty("status").GetInt32());
        Assert.Equal("http_error", problem.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Production_exception_does_not_expose_exception_details()
    {
        using var factory = new ApiFactory(postgres.Container.GetConnectionString(), "Production")
            .WithWebHostBuilder(builder => builder.ConfigureTestServices(services => services.AddSingleton<IStartupFilter, FailureFilter>()));
        using var client = factory.CreateClient();
        var response = await client.GetAsync("/api/v1/test-error");
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("synthetic-exception-sentinel", body);
        using var problem = JsonDocument.Parse(body);
        Assert.Equal("unexpected_error", problem.RootElement.GetProperty("code").GetString());
    }

    private sealed class FailureFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            next(app);
            app.Use(async (context, continuation) =>
            {
                if (context.Request.Path == "/api/v1/test-error") throw new InvalidOperationException("synthetic-exception-sentinel");
                await continuation(context);
            });
        };
    }
}
