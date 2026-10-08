using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Who.Application.Authentication;
using Who.Infrastructure.Persistence;

namespace Who.IntegrationTests;

public sealed class TestClock : TimeProvider
{
    private long ticks = DateTimeOffset.UtcNow.UtcTicks;
    public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref ticks), TimeSpan.Zero);
    public void Advance(TimeSpan span) => Interlocked.Add(ref ticks, span.Ticks);
}
public sealed class RecordingEmailSender : IVerificationEmailSender
{
    private readonly ConcurrentDictionary<string, string> codes = new(StringComparer.OrdinalIgnoreCase);
    public readonly ConcurrentBag<string> AllCodes = new();
    public int Count;
    public bool Fail;
    public string CodeFor(string email) => codes[email];
    public Task SendAsync(string email, string code, CancellationToken ct)
    {
        if (Fail) throw new InvalidOperationException("Synthetic delivery failure.");
        AllCodes.Add(code); codes[email] = code; Interlocked.Increment(ref Count); return Task.CompletedTask;
    }
}
public static class TestAuthConfiguration
{
    public static readonly string Pepper = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    public static readonly string SigningKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    public static Dictionary<string, string?> Values(bool relaxedRates = true)
    {
        var result = new Dictionary<string, string?>
        {
            ["WHO_AUTH_VERIFICATION_PEPPER"] = Pepper,
            ["WHO_AUTH_JWT_SIGNING_KEY"] = SigningKey,
            ["WHO_AUTH_JWT_ISSUER"] = "who-integration",
            ["WHO_AUTH_JWT_AUDIENCE"] = "who-integration-client"
        };
        if (relaxedRates) foreach (var policy in new[] { "REGISTER", "LOGIN", "VERIFY", "RESEND", "REFRESH" }) result["WHO_AUTH_RATE_" + policy] = "10000";
        return result;
    }
}

public sealed class RecordingLogs : ILoggerProvider
{
    public ConcurrentQueue<string> Messages { get; } = new();
    public ILogger CreateLogger(string categoryName) => new Recorder(Messages);
    public void Dispose() { }
    private sealed class Recorder(ConcurrentQueue<string> messages) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            messages.Enqueue(formatter(state, exception));
    }
}
