using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Who.Api.Authentication;
using Who.Api.Health;
using Who.Infrastructure;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddWhoInfrastructure();
builder.Services.AddWhoAuthentication();
builder.Services.AddWhoRateLimits(builder.Configuration);
builder.Services.AddExceptionHandler<AuthExceptionHandler>();
builder.Services.Configure<Microsoft.AspNetCore.Routing.RouteHandlerOptions>(options => options.ThrowOnBadRequest = true);
builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow);
builder.Services.AddHealthChecks().AddCheck<PostgresReadinessCheck>("postgres", tags: ["ready"]);
builder.Services.AddOpenApi();
builder.Services.AddProblemDetails(options => options.CustomizeProblemDetails = context =>
{
    var status = context.ProblemDetails.Status ?? context.HttpContext.Response.StatusCode;
    if (!context.ProblemDetails.Extensions.ContainsKey("code"))
        context.ProblemDetails.Extensions["code"] = status switch
        { 401 => "UNAUTHORIZED", 403 => "FORBIDDEN", >= 500 => "unexpected_error", _ => "http_error" };
    if (status >= 500 && !builder.Environment.IsDevelopment())
    {
        context.ProblemDetails.Title = "An unexpected error occurred.";
        context.ProblemDetails.Detail = null;
    }
});

var app = builder.Build();
app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
if (app.Environment.IsDevelopment()) app.MapOpenApi().AllowAnonymous();

app.MapHealthChecks("/health/live", new HealthCheckOptions
{ Predicate = _ => false, ResponseWriter = WriteHealthResponse }).AllowAnonymous();
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{ Predicate = registration => registration.Tags.Contains("ready"), ResponseWriter = WriteHealthResponse }).AllowAnonymous();
app.MapWhoAuth();
app.MapFallback("/{**path}", () => Results.NotFound()).AllowAnonymous().ExcludeFromDescription();
app.Run();

static Task WriteHealthResponse(HttpContext context, Microsoft.Extensions.Diagnostics.HealthChecks.HealthReport report) =>
    context.Response.WriteAsJsonAsync(new { status = report.Status.ToString() }, context.RequestAborted);

public partial class Program { }
