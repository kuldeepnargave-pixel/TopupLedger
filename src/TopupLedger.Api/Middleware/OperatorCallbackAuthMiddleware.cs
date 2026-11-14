using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using System.Security.Cryptography;
using System.Text;
using TopupLedger.Application.Services;
using TopupLedger.Infrastructure.Configuration;

namespace TopupLedger.Api.Middleware;

/// <summary>
/// Operator callback authentication. Each operator has its own shared secret in configuration
/// (OperatorCallbacks:Tokens:etisalat / :du). The header X-Operator-Token must match exactly.
/// Constant-time comparison.
/// </summary>
public sealed class OperatorCallbackAuthMiddleware
{
    private readonly RequestDelegate _next;

    public OperatorCallbackAuthMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext ctx, IOptions<OperatorCallbackOptions> options)
    {
        var path = ctx.Request.Path.Value ?? "";
        if (!path.StartsWith("/api/v1/operator/callbacks", StringComparison.OrdinalIgnoreCase))
        {
            await _next(ctx);
            return;
        }

        var token = ctx.Request.Headers["X-Operator-Token"].FirstOrDefault();
        if (string.IsNullOrEmpty(token))
        {
            await WriteErrorAsync(ctx, "AUTH_MISSING_HEADERS", 401, "X-Operator-Token is required.");
            return;
        }

        // Extract operator from the path: /api/v1/operator/callbacks/{operator}
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var opCode = parts.Length >= 4 ? parts[3] : null;
        if (opCode == null)
        {
            await WriteErrorAsync(ctx, "VALIDATION_FAILED", 400, "Operator path segment is missing.");
            return;
        }

        var tokens = options.Value.Tokens;
        if (!tokens.TryGetValue(opCode, out var expected) || string.IsNullOrEmpty(expected))
        {
            await WriteErrorAsync(ctx, "AUTH_INVALID_SIGNATURE", 401, "Authentication failed.");
            return;
        }

        if (!ConstantTimeEquals(token, expected))
        {
            await WriteErrorAsync(ctx, "AUTH_INVALID_SIGNATURE", 401, "Authentication failed.");
            return;
        }

        await _next(ctx);
    }

    private static bool ConstantTimeEquals(string a, string b)
    {
        if (a.Length != b.Length) return false;
        var ba = Encoding.UTF8.GetBytes(a);
        var bb = Encoding.UTF8.GetBytes(b);
        return CryptographicOperations.FixedTimeEquals(ba, bb);
    }

    private static async Task WriteErrorAsync(HttpContext ctx, string code, int status, string detail)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/problem+json";
        var payload = new
        {
            type = $"https://topupledger.dev/errors/{code}",
            title = code, status, detail, code, traceId = ctx.TraceIdentifier
        };
        await ctx.Response.WriteAsJsonAsync(payload);
    }
}
