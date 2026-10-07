using Microsoft.Extensions.Configuration;
using Npgsql;

namespace Who.Infrastructure.Configuration;

public static class DatabaseConfiguration
{
    public static string Resolve(IConfiguration configuration)
    {
        NpgsqlConnectionStringBuilder connection;
        var configured = configuration.GetConnectionString("Who");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            try { connection = new NpgsqlConnectionStringBuilder(configured); }
            catch (ArgumentException)
            {
                // Do not copy the original parser exception or secret into logs.
                throw new InvalidOperationException("ConnectionStrings:Who is invalid.");
            }
            if (string.IsNullOrWhiteSpace(connection.Host) ||
                string.IsNullOrWhiteSpace(connection.Database) ||
                string.IsNullOrWhiteSpace(connection.Username) ||
                string.IsNullOrWhiteSpace(connection.Password))
                throw new InvalidOperationException("ConnectionStrings:Who requires host, database, username and password.");
        }
        else
        {
            var database = Required(configuration, "WHO_POSTGRES_DB");
            var username = Required(configuration, "WHO_POSTGRES_USER");
            var password = Required(configuration, "WHO_POSTGRES_PASSWORD");
            var rawPort = configuration["WHO_POSTGRES_PORT"] ?? "5432";
            if (!int.TryParse(rawPort, out var port) || port is < 1 or > 65535)
                throw new InvalidOperationException("WHO_POSTGRES_PORT must be an integer between 1 and 65535.");
            connection = new NpgsqlConnectionStringBuilder
            {
                Host = configuration["WHO_POSTGRES_HOST"] ?? "127.0.0.1",
                Port = port, Database = database, Username = username, Password = password
            };
        }
        connection.Timeout = 3;
        connection.CommandTimeout = 3;
        connection.IncludeErrorDetail = false;
        connection.PersistSecurityInfo = false;
        return connection.ConnectionString;
    }

    private static string Required(IConfiguration configuration, string key) =>
        !string.IsNullOrWhiteSpace(configuration[key]) ? configuration[key]! :
        throw new InvalidOperationException($"Database configuration is required: set {key} or ConnectionStrings__Who.");
}
