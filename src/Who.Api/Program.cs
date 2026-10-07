using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Who.Api.Health;
using Who.Infrastructure;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddWhoInfrastructure();
builder.Services.AddHealthChecks().AddCheck<PostgresReadinessCheck>("postgres", tags: ["ready"]);
builder.Services.AddOpenApi();
builder.Services.AddProblemDetails(options => options.CustomizeProblemDetails = context =>
{
    var status = context.ProblemDetails.Status ?? context.HttpContext.Response.StatusCode;
    context.ProblemDetails.Extensions["code"] = status >= 500 ? "unexpected_error" : "http_error";
    if (status >= 500 && !builder.Environment.IsDevelopment())
    {
        context.ProblemDetails.Title = "An unexpected error occurred.";
        context.ProblemDetails.Detail = null;
    }
});

var app = builder.Build();
app.UseExceptionHandler();
app.UseStatusCodePages();
if (app.Environment.IsDevelopment()) app.MapOpenApi();

app.MapHealthChecks("/health/live", new HealthCheckOptions
{
    Predicate = _ => false,
    ResponseWriter = WriteHealthResponse
});
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = registration => registration.Tags.Contains("ready"),
    ResponseWriter = WriteHealthResponse
});
// Future business routes use /api/v1/...; no auth/business endpoints yet.
app.Run();

static Task WriteHealthResponse(HttpContext context, Microsoft.Extensions.Diagnostics.HealthChecks.HealthReport report) =>
    context.Response.WriteAsJsonAsync(new { status = report.Status.ToString() }, context.RequestAborted);

public partial class Program { }
