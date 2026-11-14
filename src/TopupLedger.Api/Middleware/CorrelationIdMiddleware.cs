using Microsoft.AspNetCore.Http;
using System.Diagnostics;

namespace TopupLedger.Api.Middleware;

/// <summary>
/// Assigns a correlation id to every request. Uses Activity.Current?.Id if present (so it
/// matches the trace id), falls back to a new Guid. The id is exposed in the response header
/// X-Correlation-Id and in HttpContext.Items["CorrelationId"] for structured logging.
/// </summary>
public sealed class CorrelationIdMiddleware
{
    private const string HeaderName = "X-Correlation-Id";
    private readonly RequestDelegate _next;

    public CorrelationIdMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext ctx)
    {
        var correlationId = ctx.Request.Headers[HeaderName].FirstOrDefault()
                           ?? Activity.Current?.Id
                           ?? ctx.TraceIdentifier
                           ?? Guid.NewGuid().ToString("N");

        ctx.Items["CorrelationId"] = correlationId;
        ctx.Response.Headers[HeaderName] = correlationId;
        await _next(ctx);
    }
}
