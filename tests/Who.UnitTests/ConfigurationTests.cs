using Microsoft.Extensions.Configuration;
using Npgsql;
using Who.Infrastructure.Configuration;
using System.Xml.Linq;

namespace Who.UnitTests;

public class ConfigurationTests
{
    private static Dictionary<string, string?> Valid() => new()
    {
        ["WHO_POSTGRES_DB"] = "unit_database", ["WHO_POSTGRES_USER"] = "unit_user",
        ["WHO_POSTGRES_PASSWORD"] = "synthetic-unit-value", ["WHO_POSTGRES_PORT"] = "5432"
    };
    private static IConfiguration Config(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    [Theory]
    [InlineData("WHO_POSTGRES_DB")]
    [InlineData("WHO_POSTGRES_USER")]
    [InlineData("WHO_POSTGRES_PASSWORD")]
    public void Missing_required_configuration_fails_without_echoing_values(string key)
    {
        var values = Valid(); values.Remove(key);
        var exception = Assert.Throws<InvalidOperationException>(() => DatabaseConfiguration.Resolve(Config(values)));
        Assert.Contains(key, exception.Message);
        Assert.DoesNotContain("synthetic-unit-value", exception.Message);
    }

    [Fact]
    public void Environment_fields_preserve_password_delimiters_and_disable_sensitive_details()
    {
        var values = Valid(); values["WHO_POSTGRES_PASSWORD"] = "synthetic;value=with delimiters";
        var resolved = new NpgsqlConnectionStringBuilder(DatabaseConfiguration.Resolve(Config(values)));
        Assert.Equal(values["WHO_POSTGRES_PASSWORD"], resolved.Password);
        Assert.Equal("unit_database", resolved.Database);
        Assert.False(resolved.IncludeErrorDetail);
        Assert.False(resolved.PersistSecurityInfo);
    }

    [Fact]
    public void Standard_connection_string_override_supports_external_test_configuration()
    {
        var resolved = new NpgsqlConnectionStringBuilder(DatabaseConfiguration.Resolve(Config(new()
        {
            ["ConnectionStrings:Who"] = "Host=127.0.0.1;Port=23456;Database=disposable;Username=test;Password=synthetic;Include Error Detail=true"
        })));
        Assert.Equal(23456, resolved.Port);
        Assert.Equal("disposable", resolved.Database);
        Assert.False(resolved.IncludeErrorDetail);
    }

    [Fact]
    public void Invalid_port_fails_before_database_access()
    {
        var values = Valid(); values["WHO_POSTGRES_PORT"] = "65536";
        Assert.Throws<InvalidOperationException>(() => DatabaseConfiguration.Resolve(Config(values)));
    }

    [Fact]
    public void Project_graph_preserves_domain_and_application_boundaries()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Who.slnx"))) root = root.Parent;
        Assert.NotNull(root);
        string[] References(string project) => XDocument.Load(Path.Combine(root.FullName, "src", project, project + ".csproj"))
            .Descendants("ProjectReference").Select(x => Path.GetFileNameWithoutExtension((string)x.Attribute("Include")!)).Order().ToArray();
        var domain = XDocument.Load(Path.Combine(root.FullName, "src/Who.Domain/Who.Domain.csproj"));
        Assert.Empty(References("Who.Domain"));
        Assert.Empty(domain.Descendants("PackageReference"));
        Assert.Empty(domain.Descendants("FrameworkReference"));
        Assert.Equal(["Who.Domain"], References("Who.Application"));
        Assert.Equal(["Who.Application", "Who.Domain"], References("Who.Infrastructure"));
        Assert.Equal(["Who.Application", "Who.Infrastructure"], References("Who.Api"));
    }
}
