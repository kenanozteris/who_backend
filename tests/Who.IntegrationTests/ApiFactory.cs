using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace Who.IntegrationTests;

public sealed class ApiFactory(string connectionString, string environment = "Development", bool relaxedRates = true) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(environment);
        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            var values = TestAuthConfiguration.Values(relaxedRates);
            values["ConnectionStrings:Who"] = connectionString;
            configuration.AddInMemoryCollection(values);
        });
    }
}
