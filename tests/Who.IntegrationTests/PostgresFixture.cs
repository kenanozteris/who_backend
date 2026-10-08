using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;
using Who.Infrastructure.Persistence;

namespace Who.IntegrationTests;

public sealed class PostgresFixture : IAsyncLifetime
{
    public PostgreSqlContainer Container { get; } = new PostgreSqlBuilder("postgres:18-alpine")
        .WithDatabase("who_integration").WithUsername("who_test").WithPassword(Guid.NewGuid().ToString("N")).Build();
    public async Task InitializeAsync()
    {
        await Container.StartAsync();
        await using var db = new WhoDbContext(new DbContextOptionsBuilder<WhoDbContext>().UseNpgsql(Container.GetConnectionString()).Options);
        await db.Database.MigrateAsync(); // Actual migration, never EnsureCreated.
    }
    public Task DisposeAsync() => Container.DisposeAsync().AsTask();
}
