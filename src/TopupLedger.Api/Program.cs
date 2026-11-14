using System.Text.Json.Serialization;
using Microsoft.OpenApi.Models;
using TopupLedger.Api.Middleware;
using TopupLedger.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

// TimeProvider - singleton for testability, defaults to system clock.
builder.Services.AddSingleton(TimeProvider.System);

builder.Services.AddControllers(opts =>
{
    opts.Filters.Add(new Microsoft.AspNetCore.Mvc.ProducesResponseTypeAttribute(
        typeof(Microsoft.AspNetCore.Mvc.ProblemDetails), StatusCodes.Status400BadRequest));
})
.ConfigureApiBehaviorOptions(o =>
{
    // Disable the default RFC7807 factory so we can produce our own "code" field consistently.
    o.SuppressModelStateInvalidFilter = false;
    o.InvalidModelStateResponseFactory = ctx =>
    {
        var fieldErrors = ctx.ModelState
            .Where(kv => kv.Value!.Errors.Count > 0)
            .ToDictionary(
                kv => kv.Key,
                kv => kv.Value!.Errors.Select(e => e.ErrorMessage).ToArray());

        var pd = new Microsoft.AspNetCore.Mvc.ProblemDetails
        {
            Type = "https://topupledger.dev/errors/VALIDATION_FAILED",
            Title = "VALIDATION_FAILED",
            Status = 400,
            Detail = "Request validation failed.",
            Instance = ctx.HttpContext.Request.Path
        };
        pd.Extensions["code"] = "VALIDATION_FAILED";
        pd.Extensions["errors"] = fieldErrors;
        pd.Extensions["traceId"] = ctx.HttpContext.TraceIdentifier;
        return new Microsoft.AspNetCore.Mvc.BadRequestObjectResult(pd)
        {
            ContentTypes = { "application/problem+json" }
        };
    };
})
.AddJsonOptions(o =>
{
    o.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
    o.JsonSerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
});

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo { Title = "TopupLedger API", Version = "v1" });
    c.AddSecurityDefinition("HmacPartner", new OpenApiSecurityScheme
    {
        Type = SecuritySchemeType.ApiKey,
        In = ParameterLocation.Header,
        Name = "X-Api-Key",
        Description = "Partner HMAC authentication. Headers: X-Api-Key, X-Timestamp, X-Signature, Idempotency-Key (POST)."
    });
    c.AddSecurityDefinition("AdminKey", new OpenApiSecurityScheme
    {
        Type = SecuritySchemeType.ApiKey,
        In = ParameterLocation.Header,
        Name = "X-Admin-Key",
        Description = "Admin API key for report endpoints."
    });
    c.AddSecurityDefinition("OperatorToken", new OpenApiSecurityScheme
    {
        Type = SecuritySchemeType.ApiKey,
        In = ParameterLocation.Header,
        Name = "X-Operator-Token",
        Description = "Per-operator shared secret for callback endpoints."
    });
});

builder.Services.AddCors(opts => opts.AddDefaultPolicy(p =>
    p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));

builder.Services.AddTopupLedgerInfrastructure(builder.Configuration);

var app = builder.Build();

// Pipeline (order matters).
app.UseMiddleware<CorrelationIdMiddleware>();
app.UseMiddleware<GlobalExceptionMiddleware>();
app.UseMiddleware<AdminApiKeyMiddleware>();
app.UseMiddleware<OperatorCallbackAuthMiddleware>();
app.UseMiddleware<HmacAuthMiddleware>();

app.UseCors();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// Disable HTTPS redirection in dev - we run HTTP on port 8080 inside Docker.
// In production, terminate TLS at the reverse proxy and set ASPNETCORE_FORWARDEDHEADERS_ENABLED=true.
app.Use(async (ctx, next) => { await next(); });

app.UseAuthorization();
app.MapControllers();

app.Run();

public partial class Program { /* required for WebApplicationFactory to find the entry point */ }
