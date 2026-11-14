using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TopupLedger.Application.Exceptions;

namespace TopupLedger.Api.Middleware;

/// <summary>
/// Central exception handler. Converts DomainException to RFC 7807 problem+json with the
/// extra "code" field. Catches all unhandled exceptions and returns a 500 without leaking
/// internal details (logs full exception with correlation id).
/// </summary>
public sealed class GlobalExceptionMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<GlobalExceptionMiddleware> _logger;
    private readonly IHostEnvironment _env;

    public GlobalExceptionMiddleware(RequestDelegate next, ILogger<GlobalExceptionMiddleware> logger, IHostEnvironment env)
    {
        _next = next;
        _logger = logger;
        _env = env;
    }

    public async Task InvokeAsync(HttpContext ctx)
    {
        try { await _next(ctx); }
        catch (DomainException dex)
        {
            await WriteProblemAsync(ctx, dex.Code, dex.HttpStatusCode, dex.Message, dex.FieldErrors);
        }
        catch (Exception ex)
        {
            var corrId = ctx.TraceIdentifier;
            _logger.LogError(ex, "Unhandled exception. CorrelationId={corr}", corrId);
            await WriteProblemAsync(ctx, "INTERNAL_ERROR", 500,
                _env.IsDevelopment() ? ex.Message : "An internal server error occurred.");
        }
    }

    private static async Task WriteProblemAsync(HttpContext ctx, string code, int status, string detail,
        IReadOnlyDictionary<string, string[]>? fieldErrors = null)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/problem+json";
        var problem = new ProblemDetails
        {
            Type = $"https://topupledger.dev/errors/{code}",
            Title = code,
            Status = status,
            Detail = detail,
            Instance = ctx.Request.Path
        };
        problem.Extensions["code"] = code;
        problem.Extensions["traceId"] = ctx.TraceIdentifier;
        if (fieldErrors != null && fieldErrors.Count > 0)
            problem.Extensions["errors"] = fieldErrors;

        await ctx.Response.WriteAsJsonAsync(problem);
    }
}
