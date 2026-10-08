using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Who.Application.Authentication;

namespace Who.Api.Authentication;

public sealed class AuthExceptionHandler(IProblemDetailsService problems) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is not AuthException && exception is not BadHttpRequestException) return false;
        var auth = exception as AuthException;
        var status = auth?.Status ?? 400;
        context.Response.StatusCode = status;
        var problem = new ProblemDetails { Status = status, Title = "The request could not be completed." };
        problem.Extensions["code"] = auth?.Code ?? "REQUEST_INVALID";
        if (auth is not null) foreach (var field in auth.DataFields) problem.Extensions[field.Key] = field.Value;
        return await problems.TryWriteAsync(new() { HttpContext = context, ProblemDetails = problem });
    }
}
