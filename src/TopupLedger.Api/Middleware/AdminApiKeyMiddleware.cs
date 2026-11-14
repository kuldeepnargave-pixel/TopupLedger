using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using System.Security.Cryptography;
using System.Text;
using TopupLedger.Infrastructure.Configuration;

namespace TopupLedger.Api.Middleware;

/// <summary>
/// Authenticates /api/v1/reports/* requests via the X-Admin-Key header. Partner HMAC keys
/// must NOT authenticate report requests (401). The admin key is configured via env var
/// TOPUPLEDGER_ADMIN_API_KEY or appsettings:AdminApi:AdminApiKey.
/// </summary>
public sealed class AdminApiKeyMiddleware
{
    private readonly RequestDelegate _next;

    public AdminApiKeyMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext ctx, IOptions<AdminApiOptions> options)
    {
        var path = ctx.Request.Path.Value ?? "";
        if (!path.StartsWith("/api/v1/reports", StringComparison.OrdinalIgnoreCase))
        {
            await _next(ctx);
            return;
        }

        var expected = options.Value.AdminApiKey;
        if (string.IsNullOrEmpty(expected))
        {
            ctx.Response.StatusCode = 500;
            ctx.Response.ContentType = "application/problem+json";
            await ctx.Response.WriteAsync("""{"type":"https://topupledger.dev/errors/ADMIN_NOT_CONFIGURED","title":"ADMIN_NOT_CONFIGURED","status":500,"detail":"Admin API key is not configured. Set TOPUPLEDGER_ADMIN_API_KEY.","code":"ADMIN_NOT_CONFIGURED"}""");
            return;
        }

        var provided = ctx.Request.Headers["X-Admin-Key"].FirstOrDefault();
        if (string.IsNullOrEmpty(provided) || !ConstantTimeEquals(provided, expected))
        {
            ctx.Response.StatusCode = 401;
            ctx.Response.ContentType = "application/problem+json";
            await ctx.Response.WriteAsync("""{"type":"https://topupledger.dev/errors/ADMIN_AUTH_FAILED","title":"ADMIN_AUTH_FAILED","status":401,"detail":"Admin API key is missing or invalid.","code":"ADMIN_AUTH_FAILED"}""");
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
}
