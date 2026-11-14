using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.IO.Pipelines;
using TopupLedger.Application.Exceptions;
using TopupLedger.Application.Interfaces;
using TopupLedger.Application.Services;
using TopupLedger.Domain.Entities;

namespace TopupLedger.Api.Middleware;

/// <summary>
/// HMAC-SHA256 authentication middleware for partner API requests.
///
/// Validates X-Api-Key + X-Timestamp + X-Signature (+ Idempotency-Key for POST).
/// Reads the raw request body bytes ONCE (without re-serialising JSON) so the SHA256
/// hash is computed over the exact bytes the partner signed.
///
/// On success: attaches the resolved Partner to HttpContext.Items["Partner"].
/// On failure: returns RFC 7807 problem details with the appropriate error code.
/// </summary>
public sealed class HmacAuthMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<HmacAuthMiddleware> _logger;

    public HmacAuthMiddleware(RequestDelegate next, ILogger<HmacAuthMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context, IPartnerRepository partners,
        IPartnerSecretProtector protector, TimeProvider timeProvider)
    {
        var path = context.Request.Path.Value ?? "";
        // Only authenticate partner-facing endpoints under /api/v1/partner.
        // /api/v1/operator is authenticated by a separate token, /api/v1/reports by admin key,
        // /health is open. Swagger is open in dev only.
        if (!path.StartsWith("/api/v1/partner", StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }

        // Read the raw body bytes once, buffer them so the controller can re-read.
        byte[] bodyBytes = Array.Empty<byte>();
        if (HttpMethods.IsPost(context.Request.Method) || HttpMethods.IsPut(context.Request.Method) || HttpMethods.IsPatch(context.Request.Method))
        {
            context.Request.EnableBuffering();
            await using var mem = new MemoryStream();
            await context.Request.Body.CopyToAsync(mem);
            bodyBytes = mem.ToArray();
            context.Request.Body.Position = 0;
        }

        var apiKey = context.Request.Headers["X-Api-Key"].FirstOrDefault();
        var timestamp = context.Request.Headers["X-Timestamp"].FirstOrDefault();
        var signature = context.Request.Headers["X-Signature"].FirstOrDefault();
        var idemKey = context.Request.Headers["Idempotency-Key"].FirstOrDefault();

        // For GET, Idempotency-Key is treated as empty string in the string-to-sign.
        // For POST, it is required.
        if (HttpMethods.IsPost(context.Request.Method) && string.IsNullOrEmpty(idemKey))
        {
            await WriteProblemAsync(context, "AUTH_MISSING_HEADERS", 401, "Idempotency-Key is required for POST requests.");
            return;
        }
        if (string.IsNullOrEmpty(apiKey) || string.IsNullOrEmpty(timestamp) || string.IsNullOrEmpty(signature))
        {
            await WriteProblemAsync(context, "AUTH_MISSING_HEADERS", 401,
                "Missing required authentication headers (X-Api-Key, X-Timestamp, X-Signature).");
            return;
        }

        // Resolve the partner by API key. Same error code for "unknown key" and "bad signature"
        // to avoid leaking which one is wrong.
        var partner = await partners.GetByApiKeyAsync(apiKey, context.RequestAborted);
        if (partner == null || partner.SecretCipher.Length == 0)
        {
            await WriteProblemAsync(context, "AUTH_INVALID_SIGNATURE", 401,
                "Authentication failed.");
            return;
        }

        // Clock skew check.
        if (!long.TryParse(timestamp, out var tsUnix) || !HmacSignatureService.IsTimestampValid(tsUnix, timeProvider.GetUtcNow().UtcDateTime))
        {
            await WriteProblemAsync(context, "AUTH_CLOCK_SKEW", 401,
                "X-Timestamp is outside the allowed +/- 300 second window.");
            return;
        }

        // Compute body hash and expected signature.
        string bodySha256 = HmacSignatureService.HashBody(bodyBytes);
        string stringToSign = HmacSignatureService.BuildStringToSign(
            context.Request.Method, path, timestamp, idemKey ?? "", bodySha256);

        string plaintextSecret;
        try
        {
            plaintextSecret = protector.Decrypt(partner.SecretCipher, partner.SecretNonce, partner.SecretTag);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to decrypt secret for partner {Id}", partner.Id);
            await WriteProblemAsync(context, "AUTH_INVALID_SIGNATURE", 401, "Authentication failed.");
            return;
        }
        string expectedSignature = HmacSignatureService.Compute(plaintextSecret, stringToSign);

        if (!HmacSignatureService.VerifyConstantTime(expectedSignature, signature))
        {
            _logger.LogWarning("HMAC signature mismatch for partner {Id} (api_key={key})", partner.Id, MaskKey(apiKey));
            await WriteProblemAsync(context, "AUTH_INVALID_SIGNATURE", 401, "Authentication failed.");
            return;
        }

        // Attach the resolved partner + raw body for downstream controllers / TopupService.
        context.Items["Partner"] = partner;
        context.Items["RawRequestBody"] = bodyBytes;
        context.Items["CorrelationId"] = Activity.Current?.Id ?? context.TraceIdentifier;

        await _next(context);
    }

    private static string MaskKey(string key) =>
        string.IsNullOrEmpty(key) ? "" : (key.Length <= 8 ? "***" : key.Substring(0, 4) + "..." + key[^4..]);

    private static async Task WriteProblemAsync(HttpContext ctx, string code, int status, string detail)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/problem+json";
        var payload = new
        {
            type = $"https://topupledger.dev/errors/{code}",
            title = code,
            status,
            detail,
            code,
            traceId = ctx.TraceIdentifier
        };
        await ctx.Response.WriteAsJsonAsync(payload);
    }
}
