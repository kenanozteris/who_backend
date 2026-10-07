using Testcontainers.PostgreSql;

namespace Who.IntegrationTests;

public sealed class PostgresFixture : IAsyncLifetime
{
    // Random host port, unique disposable container; no Compose database/volume.
    public PostgreSqlContainer Container { get; } = new PostgreSqlBuilder("postgres:18-alpine")
        .WithDatabase("who_integration")
        .WithUsername("who_test")
        .WithPassword(Guid.NewGuid().ToString("N"))
        .Build();
    public Task InitializeAsync() => Container.StartAsync();
    public Task DisposeAsync() => Container.DisposeAsync().AsTask();
}
